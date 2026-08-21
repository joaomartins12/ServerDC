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

            // In the retail flow CmdJoinArea reaches GameServer after the AreaServer
            // EnterArea/discovery exchange. This is therefore a much safer point for
            // packet 806 than login/bootstrap: remote player objects already exist.
            // Synchronise equipped licenses in both directions so the world-name badge
            // is not limited to the User Information window.
            SyncVisibleLicenses(packet.Sender);
        }

        private static void SyncVisibleLicenses(Client joiningClient)
        {
            if (joiningClient?.User?.ActiveCharacter == null || joiningClient.User.VehicleSerial == 0)
                return;

            var joiningCharacter = joiningClient.User.ActiveCharacter;
            var joiningLicense = GetCurrentLicense(joiningCharacter.Id);

            foreach (var other in GameServer.Instance.Server.GetClients())
            {
                if (other == null || other == joiningClient ||
                    other.User?.ActiveCharacter == null || other.User.VehicleSerial == 0)
                    continue;

                var otherCharacter = other.User.ActiveCharacter;
                var otherLicense = GetCurrentLicense(otherCharacter.Id);

                SendLicenseInfo(joiningClient, other.User.VehicleSerial, otherLicense);
                SendLicenseInfo(other, joiningClient.User.VehicleSerial, joiningLicense);

                Log.Debug(
                    "JoinArea license sync: {0}(serial={1},license={2}) <-> {3}(serial={4},license={5})",
                    joiningCharacter.Name,
                    joiningClient.User.VehicleSerial,
                    joiningLicense,
                    otherCharacter.Name,
                    other.User.VehicleSerial,
                    otherLicense);
            }
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
