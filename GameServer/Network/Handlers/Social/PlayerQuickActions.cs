using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Shared.Models;
using Shared.Network;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers.Social
{
    /// <summary>
    /// Right-click player quick actions used by Drift City v0.77a.
    /// Retail ids in this family are:
    /// 228 PartyPreCheck -> 229 PartyPreCheckAck -> 240 PartyInvite
    /// 238 FriendRequest -> 239 FriendRequestAck
    ///
    /// The old server only implemented 232 FriendAddByName, so right-click Add Friend
    /// never reached that code path. Target resolution here deliberately accepts serial,
    /// CID and UTF-16 character-name forms because different UI paths use different keys.
    /// </summary>
    public static class PlayerQuickActions
    {
        private const ushort CmdPartyPreCheck = 228;
        private const ushort CmdPartyPreCheckAck = 229;
        private const ushort CmdFriendRequest = 238;
        private const ushort CmdFriendRequestAck = 239;
        private const ushort CmdPartyInvite = 240;
        private const ushort CmdPartyReject = 241;
        private const ushort CmdPartyJoin = 242;
        private const ushort CmdPartyJoinResult = 243;

        private static readonly object PartySync = new object();
        private static readonly Dictionary<ushort, ushort> PendingPartyInvites =
            new Dictionary<ushort, ushort>();

        [Packet(CmdFriendRequest)]
        public static void FriendRequest(Packet packet)
        {
            var source = packet.Sender?.User?.ActiveCharacter;
            if (source == null) return;

            var payload = ReadRemaining(packet);
            var target = ResolveTarget(payload, packet.Sender);
            if (target?.User?.ActiveCharacter == null ||
                target.User.ActiveCharacter.Id == source.Id)
            {
                Log.Warning("FriendRequest quick-action: unable to resolve target. Source={0} Payload={1}",
                    source.Name, Hex(payload));
                return;
            }

            var targetCharacter = target.User.ActiveCharacter;
            try
            {
                using (var conn = GameServer.Instance.Database.Connection)
                {
                    AddFriendIfMissing(conn, source.Id, targetCharacter.Id);
                    AddFriendIfMissing(conn, targetCharacter.Id, source.Id);
                }

                // Echo the retail request body in the 239 ACK. This preserves whichever
                // target key variant (serial/CID/name) the client used for this UI path.
                var ack = new Packet(CmdFriendRequestAck);
                ack.Writer.Write(payload ?? new byte[0]);
                packet.Sender.Send(ack);

                RefreshFriendList(packet.Sender);
                RefreshFriendList(target);
                FriendList.PushLiveUpdate(source.Name);
                FriendList.PushLiveUpdate(targetCharacter.Name);

                Log.Info("FriendRequest quick-action: {0} <-> {1} added; payload={2}",
                    source.Name, targetCharacter.Name, Hex(payload));
            }
            catch (Exception ex)
            {
                Log.Warning("FriendRequest quick-action failed: {0} -> {1}: {2}",
                    source.Name, targetCharacter.Name, ex.Message);
            }
        }

        [Packet(CmdPartyPreCheck)]
        public static void PartyPreCheck(Packet packet)
        {
            var source = packet.Sender?.User?.ActiveCharacter;
            if (source == null) return;

            var payload = ReadRemaining(packet);
            var target = ResolveTarget(payload, packet.Sender);
            if (target?.User?.ActiveCharacter == null || target.User.VehicleSerial == 0)
            {
                Log.Warning("PartyPreCheck: unable to resolve online target. Source={0} Payload={1}",
                    source.Name, Hex(payload));
                return;
            }

            // 229 mirrors the request target key. The actual invitation delivered to the
            // target must identify the inviter instead, so patch the same retail-sized body
            // from target identity to source identity instead of inventing a new layout.
            var precheckAck = new Packet(CmdPartyPreCheckAck);
            precheckAck.Writer.Write(payload ?? new byte[0]);
            packet.Sender.Send(precheckAck);

            var inviteBody = RewriteIdentity(payload, target, packet.Sender);
            var invite = new Packet(CmdPartyInvite);
            invite.Writer.Write(inviteBody);
            target.Send(invite);

            lock (PartySync)
                PendingPartyInvites[target.User.VehicleSerial] = packet.Sender.User.VehicleSerial;

            Log.Info("Party quick-action invite: {0}(serial={1}) -> {2}(serial={3}) payload={4}",
                source.Name, packet.Sender.User.VehicleSerial,
                target.User.ActiveCharacter.Name, target.User.VehicleSerial,
                Hex(payload));
        }

        [Packet(CmdPartyReject)]
        public static void PartyReject(Packet packet)
        {
            var sourceSerial = packet.Sender?.User == null ? (ushort)0 : packet.Sender.User.VehicleSerial;
            if (sourceSerial == 0) return;

            ushort inviterSerial;
            lock (PartySync)
            {
                if (!PendingPartyInvites.TryGetValue(sourceSerial, out inviterSerial)) return;
                PendingPartyInvites.Remove(sourceSerial);
            }

            var inviter = GameServer.Instance.Server.GetClient(inviterSerial);
            if (inviter == null) return;

            var body = ReadRemaining(packet);
            var reject = new Packet(CmdPartyReject);
            reject.Writer.Write(RewriteIdentity(body, packet.Sender, inviter));
            inviter.Send(reject);

            Log.Info("Party quick-action rejected: inviterSerial={0} targetSerial={1}",
                inviterSerial, sourceSerial);
        }

        [Packet(CmdPartyJoin)]
        public static void PartyJoin(Packet packet)
        {
            var joinerSerial = packet.Sender?.User == null ? (ushort)0 : packet.Sender.User.VehicleSerial;
            if (joinerSerial == 0) return;

            ushort inviterSerial;
            lock (PartySync)
            {
                if (!PendingPartyInvites.TryGetValue(joinerSerial, out inviterSerial)) return;
                PendingPartyInvites.Remove(joinerSerial);
            }

            var inviter = GameServer.Instance.Server.GetClient(inviterSerial);
            if (inviter == null) return;

            var body = ReadRemaining(packet);

            // 243 is the retail join-result event. Preserve the client's accepted body and
            // send it to both peers; this lets the client consume its native party transition
            // without fabricating Room/Battle packets.
            var toInviter = new Packet(CmdPartyJoinResult);
            toInviter.Writer.Write(body ?? new byte[0]);
            inviter.Send(toInviter);

            var toJoiner = new Packet(CmdPartyJoinResult);
            toJoiner.Writer.Write(body ?? new byte[0]);
            packet.Sender.Send(toJoiner);

            Log.Info("Party quick-action accepted: {0}(serial={1}) + {2}(serial={3}) -> 243 both",
                inviter.User?.ActiveCharacter?.Name ?? "?", inviterSerial,
                packet.Sender.User?.ActiveCharacter?.Name ?? "?", joinerSerial);
        }

        internal static Client ResolveTarget(byte[] payload, Client source)
        {
            if (payload == null) payload = new byte[0];

            // Serial is the primary world-player identity. Search every aligned and
            // unaligned u16 offset because some client structs prepend a mode/result byte.
            for (var offset = 0; offset + 2 <= payload.Length; offset++)
            {
                var serial = BitConverter.ToUInt16(payload, offset);
                if (serial == 0 || (source?.User != null && serial == source.User.VehicleSerial)) continue;
                var client = GameServer.Instance.Server.GetClient(serial);
                if (client?.User?.ActiveCharacter != null) return client;
            }

            // Some menus use CID instead of serial.
            foreach (var candidate in GameServer.Instance.Server.GetClients())
            {
                var character = candidate?.User?.ActiveCharacter;
                if (character == null || ReferenceEquals(candidate, source)) continue;
                var cid = BitConverter.GetBytes(character.Id);
                if (IndexOf(payload, cid) >= 0) return candidate;
            }

            // Finally accept a fixed UTF-16 character name embedded in the body.
            foreach (var candidate in GameServer.Instance.Server.GetClients())
            {
                var character = candidate?.User?.ActiveCharacter;
                if (character == null || string.IsNullOrWhiteSpace(character.Name) || ReferenceEquals(candidate, source)) continue;
                var nameBytes = Encoding.Unicode.GetBytes(character.Name);
                if (IndexOf(payload, nameBytes) >= 0) return candidate;
            }

            return null;
        }

        private static byte[] RewriteIdentity(byte[] payload, Client oldIdentity, Client newIdentity)
        {
            var result = payload == null ? new byte[0] : (byte[])payload.Clone();
            if (oldIdentity?.User?.ActiveCharacter == null || newIdentity?.User?.ActiveCharacter == null)
                return result;

            ReplaceAll(result,
                BitConverter.GetBytes(oldIdentity.User.VehicleSerial),
                BitConverter.GetBytes(newIdentity.User.VehicleSerial));
            ReplaceAll(result,
                BitConverter.GetBytes(oldIdentity.User.ActiveCharacter.Id),
                BitConverter.GetBytes(newIdentity.User.ActiveCharacter.Id));

            var oldName = Encoding.Unicode.GetBytes(oldIdentity.User.ActiveCharacter.Name ?? string.Empty);
            var newName = Encoding.Unicode.GetBytes(newIdentity.User.ActiveCharacter.Name ?? string.Empty);
            if (oldName.Length > 0 && oldName.Length == newName.Length)
                ReplaceAll(result, oldName, newName);

            return result;
        }

        private static void AddFriendIfMissing(MySqlConnection conn, ulong cid, ulong friendCid)
        {
            using (var cmd = new MySqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.friends WHERE CID=@cid AND FCID=@fcid)
    INSERT INTO dbo.friends (CID,FCID,FSTATE) VALUES (@cid,@fcid,'F');", conn))
            {
                cmd.Parameters.AddWithValue("@cid", cid);
                cmd.Parameters.AddWithValue("@fcid", friendCid);
                cmd.ExecuteNonQuery();
            }
        }

        private static void RefreshFriendList(Client client)
        {
            if (client?.User?.ActiveCharacter == null) return;
            FriendList.Handle(new Packet(client, Packets.CmdFriendList, new byte[0]));
        }

        private static byte[] ReadRemaining(Packet packet)
        {
            if (packet?.Reader == null) return new byte[0];
            var stream = packet.Reader.BaseStream;
            var remaining = (int)Math.Max(0, stream.Length - stream.Position);
            return remaining == 0 ? new byte[0] : packet.Reader.ReadBytes(remaining);
        }

        private static int IndexOf(byte[] source, byte[] value)
        {
            if (source == null || value == null || value.Length == 0 || value.Length > source.Length) return -1;
            for (var i = 0; i <= source.Length - value.Length; i++)
            {
                var match = true;
                for (var j = 0; j < value.Length; j++)
                {
                    if (source[i + j] == value[j]) continue;
                    match = false;
                    break;
                }
                if (match) return i;
            }
            return -1;
        }

        private static void ReplaceAll(byte[] buffer, byte[] oldValue, byte[] newValue)
        {
            if (buffer == null || oldValue == null || newValue == null ||
                oldValue.Length == 0 || oldValue.Length != newValue.Length) return;

            var offset = 0;
            while (offset <= buffer.Length - oldValue.Length)
            {
                var match = true;
                for (var i = 0; i < oldValue.Length; i++)
                {
                    if (buffer[offset + i] == oldValue[i]) continue;
                    match = false;
                    break;
                }
                if (!match)
                {
                    offset++;
                    continue;
                }
                Buffer.BlockCopy(newValue, 0, buffer, offset, newValue.Length);
                offset += newValue.Length;
            }
        }

        private static string Hex(byte[] data)
        {
            return data == null || data.Length == 0 ? "<empty>" : BitConverter.ToString(data).Replace('-', ' ');
        }
    }
}
