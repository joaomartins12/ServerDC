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

        [Packet(Packets.CmdJoinArea)]
        public static void Handle(Packet packet)
        {
            var joinAreaPacket = new JoinAreaPacket(packet);
            packet.Sender.Send(new JoinAreaAnswer()
            {
                AreaId = joinAreaPacket.AreaId,
                Result = 1,
            }.CreatePacket());

            // In the captured retail flow CmdJoinArea reaches GameServer after the
            // AreaServer EnterArea/discovery exchange. Refresh the complete remote
            // player snapshot here, then attach packet 806 to that serial. This gives
            // the city renderer the same identity/visual data used by PlayerInfoReq,
            // instead of relying only on raw movement packets.
            SyncVisiblePlayers(packet.Sender);
        }

        private static void SyncVisiblePlayers(Client joiningClient)
        {
            if (joiningClient?.User?.ActiveCharacter == null || joiningClient.User.VehicleSerial == 0)
                return;

            var joiningCharacter = joiningClient.User.ActiveCharacter;
            var joiningSerial = joiningClient.User.VehicleSerial;
            var joiningLicense = GetCurrentLicense(joiningCharacter.Id);

            foreach (var other in GameServer.Instance.Server.GetClients())
            {
                if (other == null || other == joiningClient ||
                    other.User?.ActiveCharacter == null || other.User.VehicleSerial == 0)
                    continue;

                var otherCharacter = other.User.ActiveCharacter;
                var otherSerial = other.User.VehicleSerial;
                var otherLicense = GetCurrentLicense(otherCharacter.Id);

                // 802 is safe for visual identity. Do not use RoomNotifyChange/467 here;
                // that packet belongs to a different car-body namespace and previously
                // turned remote vehicles into invalid/tank models.
                SendPlayerSnapshot(joiningClient, otherSerial, otherCharacter);
                SendLicenseInfo(joiningClient, otherSerial, otherLicense);

                SendPlayerSnapshot(other, joiningSerial, joiningCharacter);
                SendLicenseInfo(other, joiningSerial, joiningLicense);

                Log.Debug(
                    "JoinArea player sync: {0}(serial={1},license={2}) <-> {3}(serial={4},license={5}) -> 802+806 both ways",
                    joiningCharacter.Name,
                    joiningSerial,
                    joiningLicense,
                    otherCharacter.Name,
                    otherSerial,
                    otherLicense);
            }
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
