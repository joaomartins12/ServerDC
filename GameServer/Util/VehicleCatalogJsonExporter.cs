using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    public static class VehicleCatalogJsonExporter
    {
        public static void Export(IList<VehicleList.VehicleData> vehicles)
        {
            try
            {
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Catalogs");
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "VehicleCatalog.json");

                using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine("{");
                    writer.WriteLine("  \"generatedAtUtc\": \"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\",");
                    writer.WriteLine("  \"count\": " + (vehicles == null ? 0 : vehicles.Count) + ",");
                    writer.WriteLine("  \"vehicles\": [");

                    if (vehicles != null)
                    {
                        for (var i = 0; i < vehicles.Count; i++)
                        {
                            var vehicle = vehicles[i];
                            if (vehicle == null) continue;

                            writer.WriteLine("    {");
                            N(writer, "runtimeIndex", i, true);
                            N(writer, "vehicleId", vehicle.UniqueId, true);
                            S(writer, "name", vehicle.Name, true);
                            S(writer, "type", vehicle.Type, true);
                            S(writer, "typeString", vehicle.TypeStr, true);
                            B(writer, "sellable", vehicle.Sellable, true);
                            S(writer, "grade", vehicle.Grade, true);
                            N(writer, "accel", vehicle.Acceleration, true);
                            N(writer, "speed", vehicle.Speed, true);
                            N(writer, "crash", vehicle.Crash, true);
                            N(writer, "boost", vehicle.Boost, true);
                            N(writer, "requiredLevel", vehicle.RequiredLevel, true);
                            N(writer, "level", vehicle.Level, true);
                            writer.WriteLine("      \"upgrades\": [");

                            if (vehicle.Upgrades != null)
                            {
                                for (var grade = 0; grade < vehicle.Upgrades.Count; grade++)
                                {
                                    var up = vehicle.Upgrades[grade];
                                    if (up == null) continue;
                                    writer.WriteLine("        {");
                                    N(writer, "gradeIndex", grade, true, 10);
                                    S(writer, "gradeName", "V" + (grade + 1), true, 10);
                                    S(writer, "coupon", up.Coupon, true, 10);
                                    N(writer, "accel", up.Acceleration, true, 10);
                                    N(writer, "speed", up.Speed, true, 10);
                                    N(writer, "crash", up.Crash, true, 10);
                                    N(writer, "boost", up.Boost, true, 10);
                                    N(writer, "price", up.Price, true, 10);
                                    N(writer, "sell", up.Sell, true, 10);
                                    N(writer, "closeSell", up.CloseSell, true, 10);
                                    N(writer, "upgradeMito", up.UpgradeMito, true, 10);
                                    N(writer, "efficiency", up.Efficiency, true, 10);
                                    N(writer, "capacity", up.Capacity, true, 10);
                                    N(writer, "requiredLevel", up.RequiredLevel, false, 10);
                                    writer.Write("        }");
                                    writer.WriteLine(HasLaterUpgrade(vehicle, grade) ? "," : string.Empty);
                                }
                            }

                            writer.WriteLine("      ]");
                            writer.Write("    }");
                            writer.WriteLine(HasLaterVehicle(vehicles, i) ? "," : string.Empty);
                        }
                    }

                    writer.WriteLine("  ]");
                    writer.WriteLine("}");
                }

                Log.Info("Vehicle JSON catalog exported to {0}", path);
            }
            catch (Exception ex)
            {
                Log.Warning("Unable to export VehicleCatalog.json: {0}", ex.Message);
            }
        }

        private static bool HasLaterVehicle(IList<VehicleList.VehicleData> vehicles, int index)
        {
            for (var i = index + 1; i < vehicles.Count; i++) if (vehicles[i] != null) return true;
            return false;
        }

        private static bool HasLaterUpgrade(VehicleList.VehicleData vehicle, int index)
        {
            if (vehicle.Upgrades == null) return false;
            for (var i = index + 1; i < vehicle.Upgrades.Count; i++) if (vehicle.Upgrades[i] != null) return true;
            return false;
        }

        private static void S(StreamWriter w, string name, object value, bool comma, int indent = 6)
        {
            var s = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            w.Write(new string(' ', indent) + "\"" + Escape(name) + "\": ");
            w.Write(s == null ? "null" : "\"" + Escape(s) + "\"");
            w.WriteLine(comma ? "," : string.Empty);
        }

        private static void N(StreamWriter w, string name, object value, bool comma, int indent = 6)
        {
            var raw = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            decimal number;
            var serialized = !string.IsNullOrWhiteSpace(raw) && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : "null";
            w.WriteLine(new string(' ', indent) + "\"" + Escape(name) + "\": " + serialized + (comma ? "," : string.Empty));
        }

        private static void B(StreamWriter w, string name, object value, bool comma, int indent = 6)
        {
            var raw = value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
            bool b;
            if (raw == "1") b = true;
            else if (raw == "0") b = false;
            else bool.TryParse(raw, out b);
            w.WriteLine(new string(' ', indent) + "\"" + Escape(name) + "\": " + (b ? "true" : "false") + (comma ? "," : string.Empty));
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            var sb = new StringBuilder(value.Length + 16);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
