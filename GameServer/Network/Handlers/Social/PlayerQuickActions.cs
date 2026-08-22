using System;
using System.Linq;
using System.Text;
using Shared.Models;
using Shared.Network;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers.Social
{
    /// <summary>
    /// Right-click player actions for Drift City v0.77a.
    ///
    /// Confirmed retail ids:
    /// 228 PartyPreCheck -> 229 PartyPreCheckAck -> 240 PartyInvite
    /// 238 FriendRequest -> 239 FriendRequestAck
    ///
    /// Party packet bodies are NOT interchangeable. Until the 229/240 structures are
    /// mapped byte-for-byte from DriftCity.exe, 228 is deliberately a passive probe.
    /// This prevents malformed server packets from crashing either client.
    /// </summary>
    public static class PlayerQuickActions
    {
        private const ushort CmdPartyPreCheck = 228;
        private const ushort CmdFriendRequest = 238;
        private const ushort CmdFriendRequestAck = 239;

        [Packet(CmdPartyPreCheck)]
        public static void PartyPreCheckProbe(Packet packet)
        {
            var source = packet.Sender?.User?.ActiveCharacter;
            var payload = ReadRemaining(packet);
            var target = ResolveTarget(payload, packet.Sender);

            Log.Warning(
                "PartyPreCheck(228) PROBE: Source={0} SourceSerial={1} Target={2} TargetSerial={3} PayloadLen={4} HEX={5}. No 229/240 sent until retail layouts are mapped.",
                source == null ? "?" : source.Name,
                packet.Sender?.User == null ? 0 : packet.Sender.User.VehicleSerial,
                target?.User?.ActiveCharacter == null ? "?" : target.User.ActiveCharacter.Name,
                target?.User == null ? 0 : target.User.VehicleSerial,
                payload == null ? 0 : payload.Length,
                Hex(payload));
        }

        [Packet(CmdFriendRequest)]
        public static void FriendRequest(Packet packet)
        {
            var source = packet.Sender?.User?.ActiveCharacter;
            if (source == null) return;

            var payload = ReadRemaining(packet);
            var target = ResolveTarget(payload, packet.Sender);
            if (target?.User?.ActiveCharacter == null || target.User.ActiveCharacter.Id == source.Id)
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

                // Preserve the exact request key selected by the client in the retail 239 ACK.
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

        internal static Client ResolveTarget(byte[] payload, Client source)
        {
            if (payload == null) payload = new byte[0];

            // World UI normally carries the vehicle serial. Search every offset because
            // some UI command structs prepend a byte/word selector before the identity.
            for (var offset = 0; offset + 2 <= payload.Length; offset++)
            {
                var serial = BitConverter.ToUInt16(payload, offset);
                if (serial == 0 || (source?.User != null && serial == source.User.VehicleSerial)) continue;
                var client = GameServer.Instance.Server.GetClient(serial);
                if (client?.User?.ActiveCharacter != null) return client;
            }

            foreach (var candidate in GameServer.Instance.Server.GetClients())
            {
                var character = candidate?.User?.ActiveCharacter;
                if (character == null || ReferenceEquals(candidate, source)) continue;
                var cid = BitConverter.GetBytes(character.Id);
                if (IndexOf(payload, cid) >= 0) return candidate;
            }

            foreach (var candidate in GameServer.Instance.Server.GetClients())
            {
                var character = candidate?.User?.ActiveCharacter;
                if (character == null || string.IsNullOrWhiteSpace(character.Name) || ReferenceEquals(candidate, source)) continue;
                var nameBytes = Encoding.Unicode.GetBytes(character.Name);
                if (IndexOf(payload, nameBytes) >= 0) return candidate;
            }

            return null;
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

        private static string Hex(byte[] data)
        {
            return data == null || data.Length == 0 ? "<empty>" : BitConverter.ToString(data).Replace('-', ' ');
        }
    }
}
