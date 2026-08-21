using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class PlayerInfoReq
    {
        private const ushort LicenseInfoRes = 806;
        private const int RookieLicenseId = 7000;

        [Packet(Packets.CmdPlayerInfoReq)]
        public static void Handle(Packet packet)
        {
            var request = new PlayerInfoReqPacket(packet);
            if (request.VehicleSerials.Length != 1)
            {
                Log.Error("PlayerInfoReq: expected one serial, received {0}.", request.VehicleSerials.Length);
                return;
            }

            var serial = request.VehicleSerials[0];
            var client = GameServer.Instance.Server.GetClient(serial);
            var character = client?.User?.ActiveCharacter;
            if (character == null)
            {
                Log.Error("PlayerInfoReq: no loaded character for serial {0}.", serial);
#if !DEBUG
                packet.Sender.KillConnection("Character for CmdPlayerInfoReq not found");
#else
                packet.Sender.SendError("Character not loaded!");
                packet.Sender.Send(new PlayerInfoOldAnswer
                {
                    PacketId = PlayerInfoOldAnswer.PlayerInfoOldPacketId
                }.CreatePacket());
#endif
                return;
            }

            var playerInfo = PlayerVisualSnapshotBuilder.BuildPlayerInfo(serial, character);
            packet.Sender.Send(new PlayerInfoOldAnswer
            {
                PacketId = PlayerInfoOldAnswer.PlayerInfoOldPacketId,
                PlayerInfo = playerInfo
            }.CreatePacket());

            var currentLicense = CharacterProgressModel.GetCurrentLicense(
                GameServer.Instance.Database.Connection,
                character.Id);
            if (currentLicense <= 0)
                currentLicense = RookieLicenseId;

            var licenseInfo = new Packet(LicenseInfoRes);
            licenseInfo.Writer.Write(serial);
            licenseInfo.Writer.Write((ushort)currentLicense);
            licenseInfo.Writer.Write((ushort)0);
            licenseInfo.Writer.Write((ushort)1);
            packet.Sender.Send(licenseInfo);

            Log.Debug(
                "PlayerInfoReq: Serial={0} Name={1} CarType={2} License={3} -> 802+806",
                serial,
                character.Name,
                character.ActiveCar == null ? 0u : character.ActiveCar.CarType,
                currentLicense);
        }
    }
}
