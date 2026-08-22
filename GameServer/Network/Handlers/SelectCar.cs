using System.Linq;
using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class SelectCar
    {
        [Packet(Packets.CmdSelectCar)]
        public static void Handle(Packet packet)
        {
            var user = packet.Sender.User;
            var character = user == null ? null : user.ActiveCharacter;
            if (character == null)
            {
                packet.Sender.SendError("No active character.");
                return;
            }

            var vehId = packet.Reader.ReadUInt32();
            var vehicle = character.GarageVehicles.FirstOrDefault(veh => veh.CarId == vehId);
            if (vehicle == null)
            {
                Log.Error("User tried to enter car he doesn't own!");
                packet.Sender.KillConnection("Hack attempt blocked!");
                return;
            }

            character.ActiveVehicleId = vehId;
            character.ActiveCar = vehicle;
            using (var conn = GameServer.Instance.Database.Connection)
                CharacterModel.Update(conn, character);

            // Paint/tint is persisted per vehicle and must be installed before the new
            // XiStrCarInfo is published. This also keeps the vehicle row authoritative for
            // AreaServer's 541 patcher immediately after the switch.
            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);

            packet.Sender.Send(new SelectCarAnswer
            {
                Vehicle = vehicle,
            }.CreatePacket());

            // The retail SelectCar flow follows the ACK with StatUpdate + VisualUpdate.
            // Our live world has two additional caches (809 player visual + 467 car attr),
            // therefore reuse the same authoritative visual sync used by equip/purchase.
            VisualShopWorldSync.Broadcast(user);
            CheckStat.Handle(packet);

            Log.Info("SelectCar live sync: CID={0} Serial={1} CarId={2} CarType={3} Color=0x{4:X8} Color2=0x{5:X8}",
                character.Id,
                user.VehicleSerial,
                vehicle.CarId,
                vehicle.CarType,
                vehicle.Color,
                vehicle.Color2);
        }
    }
}
