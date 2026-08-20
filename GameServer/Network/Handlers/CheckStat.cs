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

                // The eight ints at payload offsets 0x50..0x6F remain under research.
                // Keep the effective totals in both candidate groups until probes identify
                // their real semantics. Do not write anything beyond these eight values:
                // offset 0x70 is the start of XiStrEnchantBonus.
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

            VehiclePerformanceProbe.Apply(ack);

            QuietLog.Write(
                "StatUpdate",
                "CID={0} Level={1} CarDbId={2} VehicleId={3} Name={4} Grade=V{5} Source={6} Base[S={7},C={8},A={9},B={10}] Equip[S={11},C={12},A={13},B={14}] User={15} Total[S={16},C={17},A={18},B={19}] Perf8[{20},{21},{22},{23},{24},{25},{26},{27}] ProbeField={28} ProbeMode={29} ProbeRaw={30} ProbeFloat={31} Enchant[S={32},C={33},A={34},B={35},AddS={36}] Mitron[Capacity={37},Efficiency={38}] Tail[{39},{40}]",
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
                VehiclePerformanceProbe.Field,
                VehiclePerformanceProbe.Mode,
                VehiclePerformanceProbe.RawValue,
                VehiclePerformanceProbe.FloatValue,
                ack.Speed,
                ack.Crash,
                ack.Accel,
                ack.Boost,
                ack.AddSpeed,
                stats.MitronCapacity,
                stats.MitronEfficiency,
                ack.TrailingUnknown1,
                ack.TrailingUnknown2);

            VehiclePerformanceResearchExporter.Capture(character, activeCar, stats, equipped, userBonus, ack);
            packet.Sender.Send(ack.CreatePacket());
        }
    }
}
