using GameServer.Util;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class PlayerInfoReq
    {
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
                packet.Sender.Send(new PlayerInfoOldAnswer().CreatePacket());
#endif
                return;
            }

            var playerInfo = PlayerVisualSnapshotBuilder.BuildPlayerInfo(serial, character);
            packet.Sender.Send(new PlayerInfoOldAnswer
            {
                PlayerInfo = playerInfo
            }.CreatePacket());

            // Keep the client's player-info and car/visual caches synchronized.
            // The original game uses both player information and visual notifications
            // when it needs to represent another driver's car.
            if (character.ActiveCar != null)
                packet.Sender.Send(PlayerVisualSnapshotBuilder.BuildRoomNotifyChange(serial, character).CreatePacket());

            Log.Debug(
                "PlayerInfoReq: Serial={0} Name={1} CarType={2} -> 802{3}",
                serial,
                character.Name,
                character.ActiveCar == null ? 0u : character.ActiveCar.CarType,
                character.ActiveCar == null ? string.Empty : " + 467");
        }
    }
}
