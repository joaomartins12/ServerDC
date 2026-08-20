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
            var userBonus = (int)character.Level;
            var totalSpeed = stats.Speed + equipped.Speed + userBonus;
            var totalCrash = stats.Crash + equipped.Crash + userBonus;
            var totalAccel = stats.Accel + equipped.Accel + userBonus;
            var totalBoost = stats.Boost + equipped.Boost + userBonus;

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

                CharSpeed = userBonus,
                CharDurability = userBonus,
                CharAcceleration = userBonus,
                CharBoost = userBonus,

                TotalSpeed = totalSpeed,
                TotalDurability = totalCrash,
                TotalAcceleration = totalAccel,
                TotalBoost = totalBoost,

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

            // Optional runtime reverse-engineering probe. Disabled by default.
            VehiclePerformanceProbe.Apply(ack);

            QuietLog.Write(
                "StatUpdate",
                "CID={0} Level={1} CarDbId={2} VehicleId={3} Name={4} Grade=V{5} Source={6} Base[S={7},C={8},A={9},B={10}] Equip[S={11},C={12},A={13},B={14}] User={15} Total[S={16},C={17},A={18},B={19}] Perf[{20},{21},{22},{23},{24},{25},{26},{27},{28},{29}] ProbeField={30} ProbeValue={31} Mitron[Capacity={32},Efficiency={33}]",
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
                userBonus,
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
                ack.PerformanceUnknown9,
                ack.PerformanceUnknown10,
                VehiclePerformanceProbe.Field,
                VehiclePerformanceProbe.Value,
                stats.MitronCapacity,
                stats.MitronEfficiency);

            VehiclePerformanceResearchExporter.Capture(character, activeCar, stats, equipped, userBonus, ack);
            packet.Sender.Send(ack.CreatePacket());
        }
    }
}
