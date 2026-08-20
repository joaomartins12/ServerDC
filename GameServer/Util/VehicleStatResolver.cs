using System;
using System.Globalization;
using System.Linq;
using Shared;
using Shared.Models;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    internal sealed class ResolvedVehicleStats
    {
        public int Speed;
        public int Crash;
        public int Accel;
        public int Boost;
        public float MitronCapacity;
        public float MitronEfficiency;
        public string Source;
        public int VehicleId;
        public int Grade;
        public string VehicleName;
    }

    internal static class VehicleStatResolver
    {
        public static ResolvedVehicleStats Resolve(Vehicle vehicle)
        {
            if (vehicle == null)
                return null;

            ResolvedVehicleStats result;
            if (TryResolveFromDatabase(vehicle, out result))
                return result;

            if (TryResolveFromXml(vehicle, out result))
                return result;

            return new ResolvedVehicleStats
            {
                VehicleId = checked((int)vehicle.CarType),
                Grade = checked((int)vehicle.Grade),
                MitronCapacity = vehicle.MitronCapacity,
                MitronEfficiency = vehicle.MitronEfficiency,
                Source = "vehicle-instance/no-catalog"
            };
        }

        private static bool TryResolveFromDatabase(Vehicle vehicle, out ResolvedVehicleStats result)
        {
            result = null;
            try
            {
                using (var connection = GameServer.Instance.Database.Connection)
                using (var vehicleCommand = new MySqlCommand(
                    "SELECT Name, BaseSpeed, BaseCrash, BaseAccel, BaseBoost FROM dbo.vehicle_catalog WHERE VehicleId=@id AND IsEnabled=1",
                    connection))
                {
                    vehicleCommand.Parameters.AddWithValue("@id", vehicle.CarType);
                    using (var reader = vehicleCommand.ExecuteReader())
                    {
                        if (!reader.Read())
                            return false;

                        result = new ResolvedVehicleStats
                        {
                            VehicleId = checked((int)vehicle.CarType),
                            Grade = checked((int)vehicle.Grade),
                            VehicleName = reader["Name"] == DBNull.Value ? null : Convert.ToString(reader["Name"], CultureInfo.InvariantCulture),
                            Speed = DbInt(reader["BaseSpeed"]),
                            Crash = DbInt(reader["BaseCrash"]),
                            Accel = DbInt(reader["BaseAccel"]),
                            Boost = DbInt(reader["BaseBoost"]),
                            MitronCapacity = vehicle.MitronCapacity,
                            MitronEfficiency = vehicle.MitronEfficiency,
                            Source = "dbo.vehicle_catalog"
                        };
                    }
                }

                // Player vehicle grade is V1..V9, while catalog GradeIndex is 0..8.
                // Grade 0 is treated as base/no upgrade.
                var gradeIndex = vehicle.Grade > 0 ? (int)Math.Min(8u, vehicle.Grade - 1) : -1;
                if (gradeIndex < 0)
                    return true;

                using (var connection = GameServer.Instance.Database.Connection)
                using (var upgradeCommand = new MySqlCommand(
                    "SELECT Accel, Speed, Crash, Boost, Efficiency, Capacity FROM dbo.vehicle_upgrade_catalog WHERE VehicleId=@id AND GradeIndex=@grade",
                    connection))
                {
                    upgradeCommand.Parameters.AddWithValue("@id", vehicle.CarType);
                    upgradeCommand.Parameters.AddWithValue("@grade", gradeIndex);
                    using (var reader = upgradeCommand.ExecuteReader())
                    {
                        if (!reader.Read())
                            return true;

                        result.Accel = DbInt(reader["Accel"]);
                        result.Speed = DbInt(reader["Speed"]);
                        result.Crash = DbInt(reader["Crash"]);
                        result.Boost = DbInt(reader["Boost"]);
                        result.MitronEfficiency = DbFloat(reader["Efficiency"], result.MitronEfficiency);
                        result.MitronCapacity = DbFloat(reader["Capacity"], result.MitronCapacity);
                        result.Source = "dbo.vehicle_upgrade_catalog";
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("Vehicle stat DB lookup failed for CarType={0} Grade={1}: {2}", vehicle.CarType, vehicle.Grade, ex.Message);
                result = null;
                return false;
            }
        }

        private static bool TryResolveFromXml(Vehicle vehicle, out ResolvedVehicleStats result)
        {
            result = null;
            if (ServerMain.Vehicles == null)
                return false;

            var definition = ServerMain.Vehicles.FirstOrDefault(v =>
            {
                int id;
                return v != null && int.TryParse(v.UniqueId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id == vehicle.CarType;
            });
            if (definition == null)
                return false;

            result = new ResolvedVehicleStats
            {
                VehicleId = checked((int)vehicle.CarType),
                Grade = checked((int)vehicle.Grade),
                VehicleName = definition.Name,
                Speed = ParseInt(definition.Speed),
                Crash = ParseInt(definition.Crash),
                Accel = ParseInt(definition.Acceleration),
                Boost = ParseInt(definition.Boost),
                MitronCapacity = vehicle.MitronCapacity,
                MitronEfficiency = vehicle.MitronEfficiency,
                Source = "Vehicles.xml"
            };

            if (vehicle.Grade > 0 && definition.Upgrades != null && definition.Upgrades.Count > 0)
            {
                var gradeIndex = (int)Math.Min((uint)(definition.Upgrades.Count - 1), vehicle.Grade - 1);
                var upgrade = definition.Upgrades[gradeIndex];
                if (upgrade != null)
                {
                    result.Speed = ParseInt(upgrade.Speed, result.Speed);
                    result.Crash = ParseInt(upgrade.Crash, result.Crash);
                    result.Accel = ParseInt(upgrade.Acceleration, result.Accel);
                    result.Boost = ParseInt(upgrade.Boost, result.Boost);
                    result.MitronCapacity = ParseFloat(upgrade.Capacity, result.MitronCapacity);
                    result.MitronEfficiency = ParseFloat(upgrade.Efficiency, result.MitronEfficiency);
                    result.Source = "Vehicles.xml/Upgrade";
                }
            }

            return true;
        }

        private static int DbInt(object value)
        {
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static float DbFloat(object value, float fallback)
        {
            return value == null || value == DBNull.Value ? fallback : Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string raw, int fallback = 0)
        {
            int value;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static float ParseFloat(string raw, float fallback = 0f)
        {
            float value;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }
}
