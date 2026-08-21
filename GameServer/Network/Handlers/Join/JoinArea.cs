using System;
using System.Collections.Generic;
using System.Threading;
using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class JoinArea
    {
        private const ushort LicenseInfoRes = 806;
        private const int RookieLicenseId = 7000;
        private const int IdentityRefreshSeconds = 20;

        private sealed class LiveAreaState
        {
            public int AreaId;
            public int LicenseId;
            public string Name;
        }

        private static readonly object PresenceSync = new object();
        private static readonly Dictionary<ushort, LiveAreaState> LiveAreas =
            new Dictionary<ushort, LiveAreaState>();
        private static readonly Timer IdentityRefreshTimer = new Timer(
            RefreshVisiblePlayers,
            null,
            TimeSpan.FromSeconds(IdentityRefreshSeconds),
            TimeSpan.FromSeconds(IdentityRefreshSeconds));

        [Packet(Packets.CmdJoinArea)]
        public static void Handle(Packet packet)
        {
            var joinAreaPacket = new JoinAreaPacket(packet);
            packet.Sender.Send(new JoinAreaAnswer
            {
                AreaId = joinAreaPacket.AreaId,
                Result = 1,
            }.CreatePacket());

            if (packet.Sender?.User?.ActiveCharacter == null || packet.Sender.User.VehicleSerial == 0)
                return;

            var serial = packet.Sender.User.VehicleSerial;
            var character = packet.Sender.User.ActiveCharacter;
            var license = GetCurrentLicense(character.Id);

            lock (PresenceSync)
            {
                LiveAreas[serial] = new LiveAreaState
                {
                    AreaId = joinAreaPacket.AreaId,
                    LicenseId = license,
                    Name = character.Name ?? string.Empty
                };
            }

            Log.Debug("LiveArea: JOIN Name={0} Serial={1} AreaId={2} License={3}",
                character.Name, serial, joinAreaPacket.AreaId, license);

            // Only synchronize players that are confirmed to be in the SAME area.
            // The previous code sent 802/806 for every connected GameServer client,
            // including players in other maps, which can create stale remote entities.
            SyncVisiblePlayers(packet.Sender, joinAreaPacket.AreaId, "join");

            // Push the exact AreaId to online friends immediately so Friend List does
            // not briefly render a persisted City value through the wrong location table.
            global::GameServer.Network.Handlers.Social.FriendList.PushLiveUpdate(character.Name);
        }

        [Packet(Packets.CmdLeaveArea)]
        public static void Leave(Packet packet)
        {
            var serial = packet.Sender?.User == null ? (ushort)0 : packet.Sender.User.VehicleSerial;
            if (serial == 0) return;

            lock (PresenceSync)
                LiveAreas.Remove(serial);

            var name = packet.Sender.User.ActiveCharacter?.Name ?? string.Empty;
            Log.Debug("LiveArea: LEAVE Name={0} Serial={1}", name, serial);
            global::GameServer.Network.Handlers.Social.FriendList.PushLiveUpdate(name);
        }

        public static bool TryGetLiveArea(string characterName, out int areaId)
        {
            areaId = 0;
            if (string.IsNullOrWhiteSpace(characterName)) return false;

            lock (PresenceSync)
            {
                foreach (var state in LiveAreas.Values)
                {
                    if (string.Equals(state.Name, characterName, StringComparison.OrdinalIgnoreCase))
                    {
                        areaId = state.AreaId;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryGetState(ushort serial, out LiveAreaState state)
        {
            lock (PresenceSync)
                return LiveAreas.TryGetValue(serial, out state);
        }

        private static void SyncVisiblePlayers(Client joiningClient, int areaId, string reason)
        {
            if (joiningClient?.User?.ActiveCharacter == null || joiningClient.User.VehicleSerial == 0)
                return;

            var joiningCharacter = joiningClient.User.ActiveCharacter;
            var joiningSerial = joiningClient.User.VehicleSerial;
            LiveAreaState joiningState;
            if (!TryGetState(joiningSerial, out joiningState)) return;

            foreach (var other in GameServer.Instance.Server.GetClients())
            {
                if (other == null || other == joiningClient ||
                    other.User?.ActiveCharacter == null || other.User.VehicleSerial == 0)
                    continue;

                LiveAreaState otherState;
                if (!TryGetState(other.User.VehicleSerial, out otherState) || otherState.AreaId != areaId)
                    continue;

                SendIdentityPair(joiningClient, joiningState, other, otherState, reason);
            }
        }

        private static void RefreshVisiblePlayers(object ignored)
        {
            try
            {
                var clients = new List<Client>();
                foreach (var client in GameServer.Instance.Server.GetClients())
                {
                    if (client?.User?.ActiveCharacter != null && client.User.VehicleSerial != 0)
                        clients.Add(client);
                }

                for (var i = 0; i < clients.Count; i++)
                {
                    LiveAreaState aState;
                    if (!TryGetState(clients[i].User.VehicleSerial, out aState)) continue;

                    for (var j = i + 1; j < clients.Count; j++)
                    {
                        LiveAreaState bState;
                        if (!TryGetState(clients[j].User.VehicleSerial, out bState) ||
                            bState.AreaId != aState.AreaId)
                            continue;

                        SendIdentityPair(clients[i], aState, clients[j], bState, "heartbeat");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("LiveArea identity heartbeat failed: {0}", ex.Message);
            }
        }

        private static void SendIdentityPair(Client a, LiveAreaState aState,
            Client b, LiveAreaState bState, string reason)
        {
            if (a?.User?.ActiveCharacter == null || b?.User?.ActiveCharacter == null) return;

            var aSerial = a.User.VehicleSerial;
            var bSerial = b.User.VehicleSerial;

            SendPlayerSnapshot(a, bSerial, b.User.ActiveCharacter);
            SendLicenseInfo(a, bSerial, bState.LicenseId);

            SendPlayerSnapshot(b, aSerial, a.User.ActiveCharacter);
            SendLicenseInfo(b, aSerial, aState.LicenseId);

            Log.Debug(
                "LiveArea identity sync[{0}]: {1}(serial={2},area={3},license={4}) <-> {5}(serial={6},area={7},license={8}) -> 802+806",
                reason,
                a.User.ActiveCharacter.Name,
                aSerial,
                aState.AreaId,
                aState.LicenseId,
                b.User.ActiveCharacter.Name,
                bSerial,
                bState.AreaId,
                bState.LicenseId);
        }

        private static void SendPlayerSnapshot(Client recipient, ushort serial, Shared.Objects.Character character)
        {
            if (recipient == null || character == null || serial == 0) return;

            recipient.Send(new PlayerInfoOldAnswer
            {
                PlayerInfo = PlayerVisualSnapshotBuilder.BuildPlayerInfo(serial, character)
            }.CreatePacket());
        }

        private static int GetCurrentLicense(ulong cid)
        {
            var license = CharacterProgressModel.GetCurrentLicense(
                GameServer.Instance.Database.Connection,
                cid);
            return license > 0 ? license : RookieLicenseId;
        }

        private static void SendLicenseInfo(Client recipient, ushort serial, int licenseId)
        {
            if (recipient == null || serial == 0 || licenseId <= 0) return;

            var info = new Packet(LicenseInfoRes);
            info.Writer.Write(serial);
            info.Writer.Write((ushort)licenseId);
            info.Writer.Write((ushort)0);
            info.Writer.Write((ushort)1);
            recipient.Send(info);
        }
    }
}
