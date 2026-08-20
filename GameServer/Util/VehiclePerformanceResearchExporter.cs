using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Shared.Network.GameServer;
using Shared.Objects;

namespace GameServer.Util
{
    internal static class VehiclePerformanceResearchExporter
    {
        private static readonly object Sync = new object();
        private static long _sequence;

        public static void Capture(
            Character character,
            Vehicle vehicle,
            ResolvedVehicleStats vehicleStats,
            EquippedItemStats equipped,
            int userBonus,
            CheckStatAnswer ack)
        {
            if (character == null || vehicle == null || vehicleStats == null || equipped == null || ack == null)
                return;

            try
            {
                var seq = Interlocked.Increment(ref _sequence);
                var root = AppDomain.CurrentDomain.BaseDirectory;
                var dayRoot = Path.Combine(root, "Logs", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var folder = Path.Combine(dayRoot, "GameServer", "Research");
                Directory.CreateDirectory(folder);

                var csvPath = Path.Combine(folder, "VehiclePerformanceResearch.csv");
                var hexPath = Path.Combine(folder, "VehiclePerformancePackets.txt");
                var payload = ack.GetBytes();

                lock (Sync)
                {
                    if (!File.Exists(csvPath))
                    {
                        File.AppendAllText(csvPath,
                            "Seq,Timestamp,CID,CarId,CarType,VehicleName,Grade,Level," +
                            "BaseSpeed,BaseCrash,BaseAccel,BaseBoost," +
                            "PartSpeed,PartCrash,PartAccel,PartBoost," +
                            "UserSpeed,UserCrash,UserAccel,UserBoost," +
                            "TotalSpeed,TotalCrash,TotalAccel,TotalBoost," +
                            "Perf1,Perf2,Perf3,Perf4,Perf5,Perf6,Perf7,Perf8," +
                            "EnchantSpeed,EnchantCrash,EnchantAccel,EnchantBoost,EnchantAddSpeed," +
                            "Drop,Exp,MitronCapacity,MitronEfficiency,Tail1,Tail2,PayloadBytes" + Environment.NewLine,
                            Encoding.UTF8);
                    }

                    var row = string.Join(",", new[]
                    {
                        seq.ToString(CultureInfo.InvariantCulture),
                        Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                        character.Id.ToString(CultureInfo.InvariantCulture),
                        vehicle.CarId.ToString(CultureInfo.InvariantCulture),
                        vehicle.CarType.ToString(CultureInfo.InvariantCulture),
                        Csv(vehicleStats.VehicleName ?? string.Empty),
                        vehicleStats.Grade.ToString(CultureInfo.InvariantCulture),
                        character.Level.ToString(CultureInfo.InvariantCulture),
                        vehicleStats.Speed.ToString(CultureInfo.InvariantCulture),
                        vehicleStats.Crash.ToString(CultureInfo.InvariantCulture),
                        vehicleStats.Accel.ToString(CultureInfo.InvariantCulture),
                        vehicleStats.Boost.ToString(CultureInfo.InvariantCulture),
                        equipped.Speed.ToString(CultureInfo.InvariantCulture),
                        equipped.Crash.ToString(CultureInfo.InvariantCulture),
                        equipped.Accel.ToString(CultureInfo.InvariantCulture),
                        equipped.Boost.ToString(CultureInfo.InvariantCulture),
                        userBonus.ToString(CultureInfo.InvariantCulture),
                        userBonus.ToString(CultureInfo.InvariantCulture),
                        userBonus.ToString(CultureInfo.InvariantCulture),
                        userBonus.ToString(CultureInfo.InvariantCulture),
                        ack.TotalSpeed.ToString(CultureInfo.InvariantCulture),
                        ack.TotalDurability.ToString(CultureInfo.InvariantCulture),
                        ack.TotalAcceleration.ToString(CultureInfo.InvariantCulture),
                        ack.TotalBoost.ToString(CultureInfo.InvariantCulture),
                        ack.PerformanceUnknown1.ToString(CultureInfo.InvariantCulture),
                        ack.PerformanceUnknown2.ToString(CultureInfo.InvariantCulture),
                        ack.PerformanceUnknown3.ToString(CultureInfo.InvariantCulture),
                        ack.PerformanceUnknown4.ToString(CultureInfo.InvariantCulture),
                        ack.VehicleSpeed.ToString(CultureInfo.InvariantCulture),
                        ack.VehicleDurability.ToString(CultureInfo.InvariantCulture),
                        ack.VehicleAcceleration.ToString(CultureInfo.InvariantCulture),
                        ack.VehicleBoost.ToString(CultureInfo.InvariantCulture),
                        ack.Speed.ToString(CultureInfo.InvariantCulture),
                        ack.Crash.ToString(CultureInfo.InvariantCulture),
                        ack.Accel.ToString(CultureInfo.InvariantCulture),
                        ack.Boost.ToString(CultureInfo.InvariantCulture),
                        ack.AddSpeed.ToString(CultureInfo.InvariantCulture),
                        ack.Drop.ToString(CultureInfo.InvariantCulture),
                        ack.Exp.ToString(CultureInfo.InvariantCulture),
                        ack.MitronCapacity.ToString(CultureInfo.InvariantCulture),
                        ack.MitronEfficiency.ToString(CultureInfo.InvariantCulture),
                        ack.TrailingUnknown1.ToString(CultureInfo.InvariantCulture),
                        ack.TrailingUnknown2.ToString(CultureInfo.InvariantCulture),
                        payload.Length.ToString(CultureInfo.InvariantCulture)
                    });
                    File.AppendAllText(csvPath, row + Environment.NewLine, Encoding.UTF8);

                    var block = new StringBuilder();
                    block.AppendLine("============================================================");
                    block.AppendLine("Seq        : " + seq);
                    block.AppendLine("Timestamp  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    block.AppendLine("CID        : " + character.Id);
                    block.AppendLine("CarId      : " + vehicle.CarId);
                    block.AppendLine("CarType    : " + vehicle.CarType);
                    block.AppendLine("Vehicle    : " + (vehicleStats.VehicleName ?? string.Empty) + " V" + vehicleStats.Grade);
                    block.AppendLine("Base       : S=" + vehicleStats.Speed + " C=" + vehicleStats.Crash + " A=" + vehicleStats.Accel + " B=" + vehicleStats.Boost);
                    block.AppendLine("Parts      : S=" + equipped.Speed + " C=" + equipped.Crash + " A=" + equipped.Accel + " B=" + equipped.Boost);
                    block.AppendLine("User       : " + userBonus);
                    block.AppendLine("Total      : S=" + ack.TotalSpeed + " C=" + ack.TotalDurability + " A=" + ack.TotalAcceleration + " B=" + ack.TotalBoost);
                    block.AppendLine("Perf 0x50  : " +
                        ack.PerformanceUnknown1 + "," + ack.PerformanceUnknown2 + "," + ack.PerformanceUnknown3 + "," + ack.PerformanceUnknown4 + "," +
                        ack.VehicleSpeed + "," + ack.VehicleDurability + "," + ack.VehicleAcceleration + "," + ack.VehicleBoost);
                    block.AppendLine("Enchant0x70: S=" + ack.Speed + " C=" + ack.Crash + " A=" + ack.Accel + " B=" + ack.Boost + " AddS=" + ack.AddSpeed +
                                     " Drop=" + ack.Drop + " Exp=" + ack.Exp + " Capacity=" + ack.MitronCapacity + " Efficiency=" + ack.MitronEfficiency);
                    block.AppendLine("Tail 0x94  : " + ack.TrailingUnknown1 + "," + ack.TrailingUnknown2);
                    block.AppendLine("Payload len: " + payload.Length);
                    block.AppendLine("Payload HEX:");
                    block.AppendLine(ToHex(payload));
                    File.AppendAllText(hexPath, block.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Research output must never affect gameplay.
            }
        }

        private static string Csv(string value)
        {
            if (value == null) return string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            var sb = new StringBuilder(bytes.Length * 3);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                {
                    if (i % 16 == 0) sb.AppendLine();
                    else sb.Append(' ');
                }
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
