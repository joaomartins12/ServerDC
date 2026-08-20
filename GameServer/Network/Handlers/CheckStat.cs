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

            // The client already displays the grade-specific vehicle base stat and the equipped
            // part contribution separately. Character level must NOT be added to this block.
            // Example from the current client: Nevera V2 Speed=52 + Small(+1) must total 53,
            // not 54 because the character happens to be level 1.
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

                // There are two four-value groups in the still partially reverse-engineered
                // vehicle-performance section. Previous builds only populated the second one,
                // while the current client kept Maximum Speed / Time to reach / Crash Damage /
                // Boost Time at zero. Mirror the effective vehicle stats into both groups so the
                // client has the grade+equipment values regardless of which group this build reads.
                PerformanceUnknown1 = totalSpeed,
                PerformanceUnknown2 = totalCrash,
                PerformanceUnknown3 = totalAccel,
                PerformanceUnknown4 = totalBoost,
                VehicleSpeed = totalSpeed,
                VehicleDurability = totalCrash,
                VehicleAcceleration = totalAccel,
                VehicleBoost = totalBoost,

                MitronCapacity = stats.MitronCapacity,
                MitronEfficiency = stats.MitronEfficiency
            };

            Log.Info(
                "StatUpdate: CID={0} Level={1} CarDbId={2} VehicleId={3} Name={4} Grade=V{5} Source={6} Base[S={7},C={8},A={9},B={10}] Equip[S={11},C={12},A={13},B={14}] Total[S={15},C={16},A={17},B={18}] Performance1[S={19},C={20},A={21},B={22}] Performance2[S={23},C={24},A={25},B={26}] Mitron[Capacity={27},Efficiency={28}]",
                character.Id,
                character.Level,
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
                ack.PerformanceUnknown1,
                ack.PerformanceUnknown2,
                ack.PerformanceUnknown3,
                ack.PerformanceUnknown4,
                ack.VehicleSpeed,
                ack.VehicleDurability,
                ack.VehicleAcceleration,
                ack.VehicleBoost,
                stats.MitronCapacity,
                stats.MitronEfficiency);

            packet.Sender.Send(ack.CreatePacket());
        }
    }
}
