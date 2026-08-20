using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Shared.Models;
using Shared.Util;

namespace GameServer.Util
{
    internal static class VehicleKeyDatabaseExporter
    {
        private sealed class KeyMapRow
        {
            public int VehicleId;
            public int OfficialRuntimeIndex;
            public string CarName;
            public string UiName;
            public string KeyItemId;
        }

        public static void ExportExistingCatalogRows()
        {
            try
            {
                var mapPath = Path.Combine("system", "data", "VehicleKeyMap.tsv");
                if (!File.Exists(mapPath))
                {
                    Log.Warning("Vehicle key DB export skipped: {0} was not found.", mapPath);
                    return;
                }

                var map = LoadMap(mapPath);
                if (map.Count == 0)
                {
                    Log.Warning("Vehicle key DB export skipped: official key map is empty.");
                    return;
                }

                var rows = new List<string>();
                rows.Add("VehicleId,RuntimeIndex,DbName,OfficialRuntimeIndex,OfficialCarName,OfficialUiName,KeyItemId,CurrentKeyItemId,NeedsUpdate");

                using (var conn = GameServer.Instance.Database.Connection)
                {
                    const string sql = @"
IF OBJECT_ID(N'dbo.vehicle_catalog', N'U') IS NOT NULL
BEGIN
    SELECT VehicleId, RuntimeIndex, Name,
           CASE WHEN COL_LENGTH('dbo.vehicle_catalog','KeyItemId') IS NULL THEN NULL ELSE KeyItemId END AS KeyItemId
    FROM dbo.vehicle_catalog
    ORDER BY VehicleId;
END";

                    using (var cmd = new MySqlCommand(sql, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var vehicleId = Convert.ToInt32(reader["VehicleId"], CultureInfo.InvariantCulture);
                            KeyMapRow official;
                            if (!map.TryGetValue(vehicleId, out official))
                                continue;

                            var runtimeIndex = reader["RuntimeIndex"] == DBNull.Value ? string.Empty : Convert.ToString(reader["RuntimeIndex"], CultureInfo.InvariantCulture);
                            var dbName = reader["Name"] == DBNull.Value ? string.Empty : Convert.ToString(reader["Name"], CultureInfo.InvariantCulture);
                            var currentKey = reader["KeyItemId"] == DBNull.Value ? string.Empty : Convert.ToString(reader["KeyItemId"], CultureInfo.InvariantCulture);
                            var needsUpdate = !string.Equals((currentKey ?? string.Empty).Trim(), official.KeyItemId, StringComparison.OrdinalIgnoreCase);

                            rows.Add(string.Join(",", new[]
                            {
                                vehicleId.ToString(CultureInfo.InvariantCulture),
                                Csv(runtimeIndex),
                                Csv(dbName),
                                official.OfficialRuntimeIndex.ToString(CultureInfo.InvariantCulture),
                                Csv(official.CarName),
                                Csv(official.UiName),
                                Csv(official.KeyItemId),
                                Csv(currentKey),
                                needsUpdate ? "1" : "0"
                            }));
                        }
                    }
                }

                var outputDirectory = Path.Combine("Logs", "Catalogs");
                Directory.CreateDirectory(outputDirectory);
                var outputPath = Path.Combine(outputDirectory, "VehicleKeysForDatabase.csv");
                File.WriteAllLines(outputPath, rows, new UTF8Encoding(true));

                Log.Info("Vehicle key DB export: wrote {0} existing vehicle_catalog rows to {1}.", Math.Max(0, rows.Count - 1), outputPath);
            }
            catch (Exception ex)
            {
                Log.Warning("Vehicle key DB export failed: {0}", ex.Message);
            }
        }

        private static Dictionary<int, KeyMapRow> LoadMap(string path)
        {
            var result = new Dictionary<int, KeyMapRow>();
            var lines = File.ReadAllLines(path);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;

                int vehicleId;
                int runtimeIndex;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out vehicleId)) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out runtimeIndex)) runtimeIndex = -1;

                result[vehicleId] = new KeyMapRow
                {
                    VehicleId = vehicleId,
                    OfficialRuntimeIndex = runtimeIndex,
                    CarName = parts[2],
                    UiName = parts[3],
                    KeyItemId = parts[4]
                };
            }
            return result;
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
