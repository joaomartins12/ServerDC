using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;

namespace ServerManager
{
    internal static class VehicleKeyMapImporter
    {
        internal sealed class ImportResult
        {
            public int MapRows;
            public int ExistingVehicles;
            public int Updated;
            public int AlreadyCorrect;
            public int Unmatched;
            public string SourcePath;
        }

        private sealed class KeyRow
        {
            public int VehicleId;
            public string CarName;
            public string UiName;
            public string KeyItemId;
        }

        public static string DefaultPath
        {
            get
            {
                var fromRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "system", "data", "VehicleKeyMap.tsv"));
                if (File.Exists(fromRoot)) return fromRoot;
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "system", "data", "VehicleKeyMap.tsv");
            }
        }

        public static ImportResult Import(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("VehicleKeyMap.tsv was not found.", path);

            var map = LoadMap(path);
            if (map.Count == 0)
                throw new InvalidDataException("VehicleKeyMap.tsv does not contain valid vehicle key rows.");

            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = "DCServer",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity Vehicle Key Import"
            }.ConnectionString;

            var result = new ImportResult
            {
                MapRows = map.Count,
                SourcePath = Path.GetFullPath(path)
            };

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                using (var ensure = new SqlCommand(@"
IF OBJECT_ID(N'dbo.vehicle_catalog', N'U') IS NULL
    THROW 50001, 'dbo.vehicle_catalog does not exist. Import the Vehicle Catalog first.', 1;

IF COL_LENGTH('dbo.vehicle_catalog', 'KeyItemId') IS NULL
    ALTER TABLE dbo.vehicle_catalog ADD KeyItemId VARCHAR(32) NULL;", connection))
                {
                    ensure.ExecuteNonQuery();
                }

                var existing = new List<Tuple<int, string, string>>();
                using (var read = new SqlCommand("SELECT VehicleId, Name, KeyItemId FROM dbo.vehicle_catalog ORDER BY VehicleId", connection))
                using (var reader = read.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        existing.Add(Tuple.Create(
                            Convert.ToInt32(reader["VehicleId"], CultureInfo.InvariantCulture),
                            reader["Name"] == DBNull.Value ? string.Empty : Convert.ToString(reader["Name"], CultureInfo.InvariantCulture),
                            reader["KeyItemId"] == DBNull.Value ? string.Empty : Convert.ToString(reader["KeyItemId"], CultureInfo.InvariantCulture)));
                    }
                }

                result.ExistingVehicles = existing.Count;
                var uniqueNames = BuildUniqueNameMap(map.Values);

                using (var tx = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var dbRow in existing)
                        {
                            KeyRow mapped;
                            if (!map.TryGetValue(dbRow.Item1, out mapped))
                            {
                                if (string.IsNullOrWhiteSpace(dbRow.Item2) || !uniqueNames.TryGetValue(dbRow.Item2.Trim(), out mapped))
                                {
                                    result.Unmatched++;
                                    continue;
                                }
                            }

                            var desired = (mapped.KeyItemId ?? string.Empty).Trim();
                            var current = (dbRow.Item3 ?? string.Empty).Trim();
                            if (desired.Length == 0)
                            {
                                result.Unmatched++;
                                continue;
                            }

                            if (string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                            {
                                result.AlreadyCorrect++;
                                continue;
                            }

                            using (var update = new SqlCommand(
                                "UPDATE dbo.vehicle_catalog SET KeyItemId=@key, AdminUpdatedAt=SYSUTCDATETIME() WHERE VehicleId=@id", connection, tx))
                            {
                                update.Parameters.AddWithValue("@key", desired);
                                update.Parameters.AddWithValue("@id", dbRow.Item1);
                                if (update.ExecuteNonQuery() == 1)
                                    result.Updated++;
                            }
                        }

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            return result;
        }

        private static Dictionary<int, KeyRow> LoadMap(string path)
        {
            var result = new Dictionary<int, KeyRow>();
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split('\t');
                if (parts.Length < 5) continue;

                int vehicleId;
                if (!int.TryParse(parts[0].Trim().Trim('\uFEFF'), NumberStyles.Integer, CultureInfo.InvariantCulture, out vehicleId))
                    continue;

                var key = parts[4].Trim();
                if (key.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                    key = key.Substring(0, key.Length - 4);
                if (!key.StartsWith("pc_", StringComparison.OrdinalIgnoreCase))
                    continue;

                result[vehicleId] = new KeyRow
                {
                    VehicleId = vehicleId,
                    CarName = parts[2].Trim(),
                    UiName = parts[3].Trim(),
                    KeyItemId = key
                };
            }
            return result;
        }

        private static Dictionary<string, KeyRow> BuildUniqueNameMap(IEnumerable<KeyRow> rows)
        {
            var result = new Dictionary<string, KeyRow>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                AddUnique(result, duplicates, row.CarName, row);
                AddUnique(result, duplicates, row.UiName, row);
            }

            foreach (var duplicate in duplicates)
                result.Remove(duplicate);
            return result;
        }

        private static void AddUnique(IDictionary<string, KeyRow> result, ISet<string> duplicates, string name, KeyRow row)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (duplicates.Contains(name)) return;

            KeyRow existing;
            if (result.TryGetValue(name, out existing) && existing.VehicleId != row.VehicleId)
            {
                result.Remove(name);
                duplicates.Add(name);
                return;
            }
            result[name] = row;
        }
    }
}
