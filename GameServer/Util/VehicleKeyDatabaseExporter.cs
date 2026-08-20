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

        private sealed class DbVehicleRow
        {
            public int VehicleId;
            public string RuntimeIndex;
            public string Name;
            public string CurrentKeyItemId;
        }

        public static void SyncExistingCatalogRows()
        {
            try
            {
                using (var conn = GameServer.Instance.Database.Connection)
                {
                    EnsureKeyItemIdColumn(conn);

                    var mapPath = Path.Combine("system", "data", "VehicleKeyMap.tsv");
                    if (!File.Exists(mapPath))
                    {
                        Log.Warning("Vehicle key DB sync skipped after schema preparation: {0} was not found.", mapPath);
                        return;
                    }

                    var map = LoadMap(mapPath);
                    if (map.Count == 0)
                    {
                        Log.Warning("Vehicle key DB sync skipped: official key map is empty.");
                        return;
                    }

                    var uniqueNames = BuildUniqueNameMap(map.Values);
                    var dbRows = new List<DbVehicleRow>();

                    using (var cmd = new MySqlCommand(@"
SELECT VehicleId, RuntimeIndex, Name, KeyItemId
FROM dbo.vehicle_catalog
ORDER BY VehicleId;", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            dbRows.Add(new DbVehicleRow
                            {
                                VehicleId = Convert.ToInt32(reader["VehicleId"], CultureInfo.InvariantCulture),
                                RuntimeIndex = reader["RuntimeIndex"] == DBNull.Value
                                    ? string.Empty
                                    : Convert.ToString(reader["RuntimeIndex"], CultureInfo.InvariantCulture),
                                Name = reader["Name"] == DBNull.Value
                                    ? string.Empty
                                    : Convert.ToString(reader["Name"], CultureInfo.InvariantCulture),
                                CurrentKeyItemId = reader["KeyItemId"] == DBNull.Value
                                    ? string.Empty
                                    : Convert.ToString(reader["KeyItemId"], CultureInfo.InvariantCulture)
                            });
                        }
                    }

                    var updated = 0;
                    var alreadyCorrect = 0;
                    var unmatched = 0;
                    var auditRows = new List<string>
                    {
                        "VehicleId,RuntimeIndex,DbName,OfficialRuntimeIndex,OfficialCarName,OfficialUiName,KeyItemId,PreviousKeyItemId,MatchMode,Updated"
                    };

                    foreach (var dbRow in dbRows)
                    {
                        KeyMapRow official;
                        var matchMode = "VehicleId";

                        if (!map.TryGetValue(dbRow.VehicleId, out official))
                        {
                            matchMode = "Name";
                            if (string.IsNullOrWhiteSpace(dbRow.Name) ||
                                !uniqueNames.TryGetValue(dbRow.Name.Trim(), out official))
                            {
                                unmatched++;
                                Log.Warning(
                                    "Vehicle key DB sync: no official key mapping for existing vehicle_catalog row VehicleId={0} RuntimeIndex={1} Name='{2}'.",
                                    dbRow.VehicleId,
                                    dbRow.RuntimeIndex,
                                    dbRow.Name);
                                continue;
                            }
                        }

                        var previousKey = (dbRow.CurrentKeyItemId ?? string.Empty).Trim();
                        var desiredKey = (official.KeyItemId ?? string.Empty).Trim();
                        var changed = !string.Equals(previousKey, desiredKey, StringComparison.OrdinalIgnoreCase);

                        if (changed)
                        {
                            using (var update = new MySqlCommand(@"
UPDATE dbo.vehicle_catalog
SET KeyItemId=@key,
    AdminUpdatedAt=SYSUTCDATETIME()
WHERE VehicleId=@vehicleId;", conn))
                            {
                                update.Parameters.AddWithValue("@key", desiredKey);
                                update.Parameters.AddWithValue("@vehicleId", dbRow.VehicleId);
                                if (update.ExecuteNonQuery() == 1)
                                {
                                    updated++;
                                    Log.Info(
                                        "Vehicle key DB sync: VehicleId={0} RuntimeIndex={1} Name='{2}' -> KeyItemId={3} ({4}).",
                                        dbRow.VehicleId,
                                        dbRow.RuntimeIndex,
                                        dbRow.Name,
                                        desiredKey,
                                        matchMode);
                                }
                            }
                        }
                        else
                        {
                            alreadyCorrect++;
                        }

                        auditRows.Add(string.Join(",", new[]
                        {
                            dbRow.VehicleId.ToString(CultureInfo.InvariantCulture),
                            Csv(dbRow.RuntimeIndex),
                            Csv(dbRow.Name),
                            official.OfficialRuntimeIndex.ToString(CultureInfo.InvariantCulture),
                            Csv(official.CarName),
                            Csv(official.UiName),
                            Csv(desiredKey),
                            Csv(previousKey),
                            Csv(matchMode),
                            changed ? "1" : "0"
                        }));
                    }

                    var outputDirectory = Path.Combine("Logs", "Catalogs");
                    Directory.CreateDirectory(outputDirectory);
                    var outputPath = Path.Combine(outputDirectory, "VehicleKeysForDatabase.csv");
                    File.WriteAllLines(outputPath, auditRows, new UTF8Encoding(true));

                    Log.Info(
                        "Vehicle key DB sync complete: ExistingRows={0} Updated={1} AlreadyCorrect={2} Unmatched={3}. Audit={4}",
                        dbRows.Count,
                        updated,
                        alreadyCorrect,
                        unmatched,
                        outputPath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Vehicle key DB sync failed: {0}", ex.Message);
            }
        }

        private static void EnsureKeyItemIdColumn(MySqlConnection conn)
        {
            using (var ensureTable = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.vehicle_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.vehicle_catalog
    (
        VehicleId INT NOT NULL CONSTRAINT PK_vehicle_catalog PRIMARY KEY,
        RuntimeIndex INT NULL,
        Name NVARCHAR(255) NULL,
        Type VARCHAR(64) NULL,
        TypeString VARCHAR(128) NULL,
        Sellable BIT NOT NULL CONSTRAINT DF_vehicle_catalog_Sellable DEFAULT(0),
        Grade VARCHAR(32) NULL,
        BaseAccel INT NULL,
        BaseSpeed INT NULL,
        BaseCrash INT NULL,
        BaseBoost INT NULL,
        RequiredLevel INT NULL,
        Level INT NULL,
        IsEnabled BIT NOT NULL CONSTRAINT DF_vehicle_catalog_IsEnabled DEFAULT(1),
        ServerBuyPrice INT NULL,
        ServerSellPrice INT NULL,
        SourceUpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_vehicle_catalog_SourceUpdatedAt DEFAULT(SYSUTCDATETIME()),
        AdminUpdatedAt DATETIME2 NULL,
        KeyItemId VARCHAR(32) NULL
    );
END
ELSE IF COL_LENGTH(N'dbo.vehicle_catalog', N'KeyItemId') IS NULL
BEGIN
    ALTER TABLE dbo.vehicle_catalog ADD KeyItemId VARCHAR(32) NULL;
END;", conn))
            {
                ensureTable.ExecuteNonQuery();
            }

            Log.Info("Vehicle key DB sync: dbo.vehicle_catalog.KeyItemId is ready.");
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
                if (!int.TryParse((parts[0] ?? string.Empty).Trim().TrimStart('\uFEFF'), NumberStyles.Integer, CultureInfo.InvariantCulture, out vehicleId))
                    continue;
                if (!int.TryParse((parts[1] ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out runtimeIndex))
                    runtimeIndex = -1;

                var keyItemId = (parts[4] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(keyItemId))
                    continue;

                result[vehicleId] = new KeyMapRow
                {
                    VehicleId = vehicleId,
                    OfficialRuntimeIndex = runtimeIndex,
                    CarName = (parts[2] ?? string.Empty).Trim(),
                    UiName = (parts[3] ?? string.Empty).Trim(),
                    KeyItemId = keyItemId
                };
            }
            return result;
        }

        private static Dictionary<string, KeyMapRow> BuildUniqueNameMap(IEnumerable<KeyMapRow> rows)
        {
            var result = new Dictionary<string, KeyMapRow>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                AddUniqueName(result, duplicates, row.CarName, row);
                AddUniqueName(result, duplicates, row.UiName, row);
            }

            foreach (var duplicate in duplicates)
                result.Remove(duplicate);

            return result;
        }

        private static void AddUniqueName(
            IDictionary<string, KeyMapRow> result,
            ISet<string> duplicates,
            string name,
            KeyMapRow row)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (duplicates.Contains(name)) return;

            KeyMapRow existing;
            if (result.TryGetValue(name, out existing) && existing.VehicleId != row.VehicleId)
            {
                duplicates.Add(name);
                result.Remove(name);
                return;
            }

            result[name] = row;
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
