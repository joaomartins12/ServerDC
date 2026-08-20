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

            // Character level is a permanent contribution to every effective vehicle stat.
            // The vehicle/catalog value remains the base block and equipped parts remain the
            // equipment block; level is applied exactly once when calculating the totals.
            var levelBonus = (int)character.Level;
            var totalSpeed = stats.Speed + equipped.Speed + levelBonus;
            var totalCrash = stats.Crash + equipped.Crash + levelBonus;
            var totalAccel = stats.Accel + equipped.Accel + levelBonus;
            var totalBoost = stats.Boost + equipped.Boost + levelBonus;

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
                "StatUpdate: CID={0} Level={1} CarDbId={2} VehicleId={3} Name={4} Grade=V{5} Source={6} Base[S={7},C={8},A={9},B={10}] Equip[S={11},C={12},A={13},B={14}] LevelBonus={15} Total[S={16},C={17},A={18},B={19}] Performance[S={20},C={21},A={22},B={23}] Mitron[Capacity={24},Efficiency={25}]",
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
                levelBonus,
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
