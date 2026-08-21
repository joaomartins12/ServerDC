using System;
using System.Collections.Generic;
using System.Threading;
using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class JoinArea
    {
        private const ushort LicenseInfoRes = 806;
        private const int RookieLicenseId = 7000;
        private const int JoinResyncDelayMs = 750;

        private sealed class LiveAreaState
        {
            public int AreaId;
            public int LicenseId;
            public string Name;
        }

        private static readonly object PresenceSync = new object();
        private static readonly Dictionary<ushort, LiveAreaState> LiveAreas =
            new Dictionary<ushort, LiveAreaState>();

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
                var stale = new List<ushort>();
                foreach (var pair in LiveAreas)
                {
                    if (pair.Key != serial &&
                        string.Equals(pair.Value.Name, character.Name, StringComparison.OrdinalIgnoreCase))
                        stale.Add(pair.Key);
                }

                foreach (var staleSerial in stale)
                {
                    LiveAreas.Remove(staleSerial);
                    Log.Debug("LiveArea: PURGE_STALE Name={0} OldSerial={1} NewSerial={2}",
                        character.Name, staleSerial, serial);
                }

                LiveAreas[serial] = new LiveAreaState
                {
                    AreaId = joinAreaPacket.AreaId,
                    LicenseId = license,
                    Name = character.Name ?? string.Empty
                };
            }

            Log.Debug("LiveArea: JOIN Name={0} Serial={1} AreaId={2} License={3}",
                character.Name, serial, joinAreaPacket.AreaId, license);

            SyncVisiblePlayers(packet.Sender, joinAreaPacket.AreaId, "join");
            QueueJoinResync(serial, joinAreaPacket.AreaId);

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

        private static void QueueJoinResync(ushort serial, int areaId)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(JoinResyncDelayMs);

                try
                {
                    LiveAreaState state;
                    if (!TryGetState(serial, out state) || state.AreaId != areaId)
                        return;

                    var client = GameServer.Instance.Server.GetClient(serial);
                    if (!IsCurrentSerialOwner(client) || client.User.ActiveCharacter == null)
                        return;

                    // AreaServer replays/announces packet 541 after 250 ms. By this point
                    // the local 3D vehicle should exist, so resend the full local car state.
                    SendOwnWorldSnapshot(client);

                    // Repeat remote identity + live visual + world attribute snapshots after
                    // packet 541 has instantiated the other vehicles. Sending these only at
                    // JoinArea time races the world manager and leaves default remote cars.
                    SyncVisiblePlayers(client, areaId, "join-ready");
                }
                catch (Exception ex)
                {
                    Log.Warning("LiveArea delayed join sync failed Serial={0} Area={1}: {2}",
                        serial, areaId, ex.Message);
                }
            });
        }

        private static void SendOwnWorldSnapshot(Client client)
        {
            if (!IsCurrentSerialOwner(client) || client.User.ActiveCharacter == null ||
                client.User.ActiveCharacter.ActiveCar == null)
                return;

            var character = client.User.ActiveCharacter;
            var vehicle = character.ActiveCar;
            var serial = client.User.VehicleSerial;

            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);
            var effectiveColor = vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor;

            client.Send(new VisualUpdateAnswer
            {
                Serial = serial,
                Age = 0,
                CarId = vehicle.CarId,
                VisualState = 1,
                CarInfo = new XiStrCarInfo
                {
                    CarID = vehicle.CarId,
                    CarType = vehicle.CarType,
                    BaseColor = effectiveColor,
                    Grade = vehicle.Grade,
                    SlotType = vehicle.SlotType,
                    AuctionCnt = vehicle.AuctionCnt,
                    Mitron = vehicle.Mitron,
                    Kmh = vehicle.Kmh,
                    Color = effectiveColor,
                    Color2 = vehicle.Color2,
                    MitronCapacity = vehicle.MitronCapacity,
                    MitronEfficiency = vehicle.MitronEfficiency,
                    AuctionOn = vehicle.AuctionOn,
                    SBBOn = vehicle.SBBOn
                }
            }.CreatePacket());

            client.Send(PlayerVisualSnapshotBuilder.BuildRoomNotifyChange(serial, character).CreatePacket());

            Log.Debug(
                "LiveArea self visual sync: Name={0} Serial={1} CarId={2} Color=0x{3:X6} Color2=0x{4:X8} -> 1061+467",
                character.Name, serial, vehicle.CarId, effectiveColor, vehicle.Color2);
        }

        private static bool TryGetState(ushort serial, out LiveAreaState state)
        {
            lock (PresenceSync)
                return LiveAreas.TryGetValue(serial, out state);
        }

        private static bool IsCurrentSerialOwner(Client client)
        {
            if (client?.User == null || client.User.VehicleSerial == 0) return false;
            Shared.Objects.User active;
            return DefaultServer.ActiveSerials.TryGetValue(client.User.VehicleSerial, out active) &&
                   ReferenceEquals(active, client.User);
        }

        private static void SyncVisiblePlayers(Client joiningClient, int areaId, string reason)
        {
            if (!IsCurrentSerialOwner(joiningClient) || joiningClient.User.ActiveCharacter == null)
                return;

            var joiningSerial = joiningClient.User.VehicleSerial;
            LiveAreaState joiningState;
            if (!TryGetState(joiningSerial, out joiningState)) return;

            foreach (var other in GameServer.Instance.Server.GetClients())
            {
                if (other == null || other == joiningClient || !IsCurrentSerialOwner(other) ||
                    other.User.ActiveCharacter == null)
                    continue;

                LiveAreaState otherState;
                if (!TryGetState(other.User.VehicleSerial, out otherState) || otherState.AreaId != areaId)
                    continue;

                SendIdentityPair(joiningClient, joiningState, other, otherState, reason);
            }
        }

        private static void SendIdentityPair(Client a, LiveAreaState aState,
            Client b, LiveAreaState bState, string reason)
        {
            if (!IsCurrentSerialOwner(a) || !IsCurrentSerialOwner(b) ||
                a.User.ActiveCharacter == null || b.User.ActiveCharacter == null) return;

            var aSerial = a.User.VehicleSerial;
            var bSerial = b.User.VehicleSerial;

            SendPlayerSnapshot(a, bSerial, b.User.ActiveCharacter);
            SendLicenseInfo(a, bSerial, bState.LicenseId);

            SendPlayerSnapshot(b, aSerial, a.User.ActiveCharacter);
            SendLicenseInfo(b, aSerial, aState.LicenseId);

            Log.Debug(
                "LiveArea identity sync[{0}]: {1}(serial={2},area={3},license={4}) <-> {5}(serial={6},area={7},license={8}) -> 802+809+467+806",
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

        private static void SendPlayerSnapshot(Client recipient, ushort serial, Character character)
        {
            if (recipient == null || character == null || serial == 0) return;

            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);
            var snapshot = PlayerVisualSnapshotBuilder.BuildPlayerInfo(serial, character);

            // 802 establishes identity/discovery metadata.
            recipient.Send(new PlayerInfoOldAnswer
            {
                PacketId = PlayerInfoOldAnswer.PlayerInfoOldPacketId,
                PlayerInfo = snapshot
            }.CreatePacket());

            // 809 updates the live XiPlayerInfo/XiVisualItem cache used by already-spawned
            // remote cars. This carries body kit/aero/wheel/neon/decal state that 467 alone
            // was not reliably applying in free-roam.
            recipient.Send(new PlayerInfoOldAnswer
            {
                PacketId = PlayerInfoOldAnswer.PlayerInfoLivePacketId,
                PlayerInfo = snapshot
            }.CreatePacket());

            // 467 carries the world car attributes including current paint/tint and the
            // same player snapshot, after the remote vehicle exists.
            recipient.Send(PlayerVisualSnapshotBuilder.BuildRoomNotifyChange(serial, character).CreatePacket());
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
