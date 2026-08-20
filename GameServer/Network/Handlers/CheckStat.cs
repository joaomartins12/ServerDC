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

            var equipped = EquippedItemStatResolver.Resolve(character, activeCar);
            var totalSpeed = stats.Speed + equipped.Speed;
            var totalCrash = stats.Crash + equipped.Crash;
            var totalAccel = stats.Accel + equipped.Accel;
            var totalBoost = stats.Boost + equipped.Boost;

            var ack = new CheckStatAnswer
            {
                BasedSpeed = stats.Speed,
                BasedDurability = stats.Crash,
                BasedAcceleration = stats.Accel,
                BasedBoost = stats.Boost,

                EquipSpeed = equipped.Speed,
                EquipDurability = equipped.Crash,
                EquipAcceleration = equipped.Accel,
                EquipBoost = equipped.Boost,

                TotalSpeed = totalSpeed,
                TotalDurability = totalCrash,
                TotalAcceleration = totalAccel,
                TotalBoost = totalBoost,

                // These are a separate vehicle-performance block in StatUpdateAck.
                // Sending the effective totals here lets the client calculate its
                // Maximum Speed / Time to reach / Crash Damage / Boost Time panel.
                VehicleSpeed = totalSpeed,
                VehicleDurability = totalCrash,
                VehicleAcceleration = totalAccel,
                VehicleBoost = totalBoost,

                MitronCapacity = stats.MitronCapacity,
                MitronEfficiency = stats.MitronEfficiency
            };

            Log.Info(
                "StatUpdate: CID={0} CarDbId={1} VehicleId={2} Name={3} Grade=V{4} Source={5} Base[S={6},C={7},A={8},B={9}] Equip[S={10},C={11},A={12},B={13}] Total[S={14},C={15},A={16},B={17}] Performance[S={18},C={19},A={20},B={21}] Mitron[Capacity={22},Efficiency={23}]",
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
                equipped.Speed,
                equipped.Crash,
                equipped.Accel,
                equipped.Boost,
                totalSpeed,
                totalCrash,
                totalAccel,
                totalBoost,
                totalSpeed,
                totalCrash,
                totalAccel,
                totalBoost,
                stats.MitronCapacity,
                stats.MitronEfficiency);

            packet.Sender.Send(ack.CreatePacket());
        }
    }
}
