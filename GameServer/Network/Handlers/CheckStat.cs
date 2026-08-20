using GameServer.Util;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class CheckStat
    {
        [Packet(Packets.CmdCheckStat)]
        public static void Handle(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null)
            {
                Log.Warning("CmdCheckStat ignored: no active character.");
                return;
            }

            var activeCar = character.ActiveCar;
            if (activeCar == null && character.GarageVehicles != null)
                activeCar = character.GarageVehicles.Find(v => v != null && v.CarId == character.ActiveVehicleId);

            if (activeCar == null)
            {
                Log.Warning("CmdCheckStat: CID={0} has no active vehicle (ActiveVehicleId={1}).", character.Id, character.ActiveVehicleId);
                packet.Sender.Send(new CheckStatAnswer().CreatePacket());
                return;
            }

            var stats = VehicleStatResolver.Resolve(activeCar);
            if (stats == null)
            {
                Log.Warning("CmdCheckStat: unable to resolve stats for CID={0} CarId={1} CarType={2} Grade={3}.",
                    character.Id, activeCar.CarId, activeCar.CarType, activeCar.Grade);
                packet.Sender.Send(new CheckStatAnswer().CreatePacket());
                return;
            }

            // Parts are deliberately kept separate from the base vehicle values. Once CmdEquipItem/UnEquipItem
            // is decoded, Equip* will contain the sum of the parts currently attached to this CarId.
            var equipSpeed = 0;
            var equipCrash = 0;
            var equipAccel = 0;
            var equipBoost = 0;

            var ack = new CheckStatAnswer
            {
                BasedSpeed = stats.Speed,
                BasedDurability = stats.Crash,
                BasedAcceleration = stats.Accel,
                BasedBoost = stats.Boost,

                EquipSpeed = equipSpeed,
                EquipDurability = equipCrash,
                EquipAcceleration = equipAccel,
                EquipBoost = equipBoost,

                TotalSpeed = stats.Speed + equipSpeed,
                TotalDurability = stats.Crash + equipCrash,
                TotalAcceleration = stats.Accel + equipAccel,
                TotalBoost = stats.Boost + equipBoost,

                MitronCapacity = stats.MitronCapacity,
                MitronEfficiency = stats.MitronEfficiency
            };

            Log.Info(
                "StatUpdate: CID={0} CarDbId={1} VehicleId={2} Name={3} Grade=V{4} Source={5} Base[S={6},C={7},A={8},B={9}] Mitron[Capacity={10},Efficiency={11}]",
                character.Id,
                activeCar.CarId,
                stats.VehicleId,
                string.IsNullOrWhiteSpace(stats.VehicleName) ? "UNKNOWN" : stats.VehicleName,
                stats.Grade,
                stats.Source,
                stats.Speed,
                stats.Crash,
                stats.Accel,
                stats.Boost,
                stats.MitronCapacity,
                stats.MitronEfficiency);

            packet.Sender.Send(ack.CreatePacket());
        }
    }
}
