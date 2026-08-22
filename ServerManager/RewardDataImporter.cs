using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerManager
{
    internal static class RewardDataImporter
    {
        internal sealed class ImportResult
        {
            public int SourceFiles;
            public int SourceRows;
            public int UseItemMappings;
            public int RewardEntries;
            public int VisualBoxMappings;
            public string Folder;
        }

        private sealed class TdfTable
        {
            public readonly List<string[]> Rows = new List<string[]>();
        }

        private sealed class RewardEntry
        {
            public string GroupId;
            public int Sequence;
            public string RewardType;
            public string RewardCode;
            public int Quantity;
            public int Period;
            public decimal Probability;
            public int SourceRow;
            public string RawRow;
        }

        private sealed class UseItemMap
        {
            public string ItemCode;
            public string FunctionName;
            public string RewardGroupId;
            public int SourceRow;
        }

        private sealed class VisualBoxMap
        {
            public int ShopId;
            public string VisualItemCode;
            public string UseItemCode;
            public int Quantity;
            public string RewardGroupId;
            public string Param;
            public int SourceRow;
        }

        public static string ImportFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RewardImporter"); }
        }

        public static string EnsureImportDirectory()
        {
            Directory.CreateDirectory(ImportFolder);
            return ImportFolder;
        }

        public static string[] GetMissingRequiredFiles()
        {
            EnsureImportDirectory();
            var required = new[] { "RewardGroup.xlt", "VisualItem.xlt", "UseItemClient.tdf" };
            return required.Where(name => !File.Exists(Path.Combine(ImportFolder, name))).ToArray();
        }

        public static void EnsureSchema()
        {
            using (var connection = OpenConnection())
            using (var command = new SqlCommand(GetSchemaSql(), connection))
            {
                command.CommandTimeout = 120;
                command.ExecuteNonQuery();
            }
        }

        public static ImportResult ImportAll()
        {
            var folder = EnsureImportDirectory();
            var missing = GetMissingRequiredFiles();
            if (missing.Length != 0)
                throw new InvalidOperationException("Missing required reward data: " + string.Join(", ", missing) + ".");

            var rewardPath = Path.Combine(folder, "RewardGroup.xlt");
            var visualPath = Path.Combine(folder, "VisualItem.xlt");
            var useItemPath = Path.Combine(folder, "UseItemClient.tdf");

            var rewardRows = ReadXlt(rewardPath);
            var visualRows = ReadXlt(visualPath);
            var useItemRows = ReadTdf(useItemPath).Rows;

            var useMaps = ParseUseItemMappings(useItemRows);
            var rewardEntries = ParseRewardEntries(rewardRows);
            var useMapByItem = useMaps
                .GroupBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var visualMaps = ParseVisualBoxMappings(visualRows, useMapByItem);

            if (useMaps.Count == 0)
                throw new InvalidDataException("UseItemClient.tdf was parsed, but no giftbox/luckybag reward mappings were found.");
            if (rewardEntries.Count == 0)
                throw new InvalidDataException("RewardGroup.xlt was parsed, but no reward entries were recognized.");
            if (visualMaps.Count == 0)
                throw new InvalidDataException("VisualItem.xlt was parsed, but no visual-shop box mappings were recognized.");

            using (var connection = OpenConnection())
            using (var tx = connection.BeginTransaction())
            {
                try
                {
                    using (var schema = new SqlCommand(GetSchemaSql(), connection, tx))
                    {
                        schema.CommandTimeout = 120;
                        schema.ExecuteNonQuery();
                    }

                    using (var clear = new SqlCommand(@"
DELETE FROM dbo.visual_box_map;
DELETE FROM dbo.reward_group_items;
DELETE FROM dbo.reward_use_item_map;
DELETE FROM dbo.reward_source_cells;
DELETE FROM dbo.reward_import_manifest;", connection, tx))
                        clear.ExecuteNonQuery();

                    InsertSourceCells(connection, tx, "RewardGroup.xlt", rewardRows);
                    InsertSourceCells(connection, tx, "VisualItem.xlt", visualRows);
                    InsertSourceCells(connection, tx, "UseItemClient.tdf", useItemRows);
                    InsertManifest(connection, tx, "RewardGroup.xlt", rewardRows.Count);
                    InsertManifest(connection, tx, "VisualItem.xlt", visualRows.Count);
                    InsertManifest(connection, tx, "UseItemClient.tdf", useItemRows.Count);
                    InsertUseItemMappings(connection, tx, useMaps);
                    InsertRewardEntries(connection, tx, rewardEntries);
                    InsertVisualMappings(connection, tx, visualMaps);

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }

            return new ImportResult
            {
                SourceFiles = 3,
                SourceRows = rewardRows.Count + visualRows.Count + useItemRows.Count,
                UseItemMappings = useMaps.Count,
                RewardEntries = rewardEntries.Count,
                VisualBoxMappings = visualMaps.Count,
                Folder = folder
            };
        }

        private static SqlConnection OpenConnection()
        {
            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = "DCServer",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity Reward Data Importer"
            }.ConnectionString;
            var connection = new SqlConnection(connectionString);
            connection.Open();
            return connection;
        }

        internal static string GetSchemaSql()
        {
            return @"
IF OBJECT_ID(N'dbo.reward_import_manifest', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.reward_import_manifest
    (
        SourceFile NVARCHAR(128) NOT NULL CONSTRAINT PK_reward_import_manifest PRIMARY KEY,
        RowCount INT NOT NULL,
        ImportedAt DATETIME2 NOT NULL CONSTRAINT DF_reward_import_manifest_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.reward_source_cells', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.reward_source_cells
    (
        SourceFile NVARCHAR(128) NOT NULL,
        RowIndex INT NOT NULL,
        ColumnIndex INT NOT NULL,
        CellValue NVARCHAR(MAX) NULL,
        CONSTRAINT PK_reward_source_cells PRIMARY KEY(SourceFile,RowIndex,ColumnIndex)
    );
END;

IF OBJECT_ID(N'dbo.reward_use_item_map', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.reward_use_item_map
    (
        ItemCode NVARCHAR(128) NOT NULL CONSTRAINT PK_reward_use_item_map PRIMARY KEY,
        FunctionName NVARCHAR(64) NULL,
        RewardGroupId NVARCHAR(128) NOT NULL,
        SourceRowIndex INT NOT NULL,
        ImportedAt DATETIME2 NOT NULL CONSTRAINT DF_reward_use_item_map_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
    CREATE INDEX IX_reward_use_item_map_Group ON dbo.reward_use_item_map(RewardGroupId);
END;

IF OBJECT_ID(N'dbo.reward_group_items', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.reward_group_items
    (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_reward_group_items PRIMARY KEY,
        RewardGroupId NVARCHAR(128) NOT NULL,
        Sequence INT NOT NULL,
        RewardType NVARCHAR(64) NULL,
        RewardCode NVARCHAR(128) NULL,
        Quantity INT NOT NULL CONSTRAINT DF_reward_group_items_Quantity DEFAULT(1),
        Period INT NOT NULL CONSTRAINT DF_reward_group_items_Period DEFAULT(0),
        Probability DECIMAL(18,6) NOT NULL CONSTRAINT DF_reward_group_items_Probability DEFAULT(0),
        SourceRowIndex INT NOT NULL,
        RawRow NVARCHAR(MAX) NULL,
        ImportedAt DATETIME2 NOT NULL CONSTRAINT DF_reward_group_items_ImportedAt DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT UQ_reward_group_items_GroupSequence UNIQUE(RewardGroupId,Sequence)
    );
    CREATE INDEX IX_reward_group_items_Group ON dbo.reward_group_items(RewardGroupId);
END;

IF OBJECT_ID(N'dbo.visual_box_map', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.visual_box_map
    (
        ShopId INT NOT NULL CONSTRAINT PK_visual_box_map PRIMARY KEY,
        VisualItemCode NVARCHAR(128) NULL,
        UseItemCode NVARCHAR(128) NOT NULL,
        Quantity INT NOT NULL CONSTRAINT DF_visual_box_map_Quantity DEFAULT(1),
        RewardGroupId NVARCHAR(128) NOT NULL,
        Param NVARCHAR(512) NULL,
        SourceRowIndex INT NOT NULL,
        ImportedAt DATETIME2 NOT NULL CONSTRAINT DF_visual_box_map_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
    CREATE INDEX IX_visual_box_map_UseItem ON dbo.visual_box_map(UseItemCode);
END;

IF OBJECT_ID(N'dbo.character_service_buffs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.character_service_buffs
    (
        CharacterId BIGINT NOT NULL,
        CategoryIndex INT NOT NULL,
        ShopId INT NOT NULL,
        StartedAt BIGINT NOT NULL,
        ExpireTime BIGINT NOT NULL,
        PeriodDays INT NOT NULL,
        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_character_service_buffs_UpdatedAt DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT PK_character_service_buffs PRIMARY KEY(CharacterId,CategoryIndex)
    );
    CREATE INDEX IX_character_service_buffs_Expire ON dbo.character_service_buffs(CharacterId,ExpireTime);
END;";
        }

        private static List<UseItemMap> ParseUseItemMappings(List<string[]> rows)
        {
            var result = new List<UseItemMap>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.Length == 0) continue;
                var itemCode = Cell(row, 0).Trim();
                if (string.IsNullOrEmpty(itemCode) || itemCode.StartsWith("#", StringComparison.Ordinal)) continue;

                string group = null;
                string function = null;
                foreach (var raw in row)
                {
                    var cell = (raw ?? string.Empty).Trim();
                    if (group == null && cell.StartsWith("rw_", StringComparison.OrdinalIgnoreCase)) group = cell;
                    if (function == null && IsGiftFunction(cell)) function = cell;
                }

                if (group == null) continue;
                if (function == null)
                {
                    var category = Cell(row, 1).Trim();
                    if (!IsGiftFunction(category) && category.IndexOf("giftbox", StringComparison.OrdinalIgnoreCase) < 0 &&
                        category.IndexOf("lucky", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    function = category;
                }

                result.Add(new UseItemMap
                {
                    ItemCode = itemCode,
                    FunctionName = function,
                    RewardGroupId = group,
                    SourceRow = i
                });
            }
            return result.GroupBy(x => x.ItemCode, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        }

        private static bool IsGiftFunction(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim().ToLowerInvariant();
            return text == "giftbox" || text == "drop_giftbox" || text == "giftbox_auto" ||
                   text == "giftbox_lv" || text == "giftbox_car" || text == "luckybag" ||
                   text.Contains("giftbox") || text.Contains("luckybag");
        }

        private static List<RewardEntry> ParseRewardEntries(List<string[]> rows)
        {
            var result = new List<RewardEntry>();
            if (rows.Count == 0) return result;

            var headerIndex = FindRewardHeader(rows);
            Dictionary<string, int> header = null;
            if (headerIndex >= 0) header = BuildHeaderMap(rows[headerIndex]);
            var sequence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var rowIndex = headerIndex >= 0 ? headerIndex + 1 : 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (row == null || row.Length == 0) continue;

                string group = null;
                string type = null;
                string code = null;
                int quantity = 1;
                int period = 0;
                decimal probability = 0m;

                if (header != null)
                {
                    group = FirstHeaderValue(row, header, "rewardgroupid", "rewardgroup", "groupid", "group", "rewardgroupname");
                    type = FirstHeaderValue(row, header, "rewardtype", "type", "rewardkind", "kind");
                    code = FirstHeaderValue(row, header, "rewardcode", "itemcode", "itemid", "item", "rewarditem", "rewardid", "value");
                    quantity = ParseInt(FirstHeaderValue(row, header, "quantity", "count", "amount", "num", "stacknum"), 1);
                    period = ParseInt(FirstHeaderValue(row, header, "period", "day", "days", "duration"), 0);
                    probability = ParseDecimal(FirstHeaderValue(row, header, "probability", "prob", "rate", "chance", "percent"), 0m);
                }

                if (string.IsNullOrWhiteSpace(group))
                    group = row.Select(x => (x ?? string.Empty).Trim()).FirstOrDefault(x => x.StartsWith("rw_", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(group)) continue;

                if (string.IsNullOrWhiteSpace(type))
                    type = row.Select(x => (x ?? string.Empty).Trim()).FirstOrDefault(IsRewardType);
                if (string.IsNullOrWhiteSpace(code))
                    code = row.Select(x => (x ?? string.Empty).Trim()).FirstOrDefault(IsRewardCode);

                var numeric = new List<decimal>();
                foreach (var raw in row)
                {
                    decimal number;
                    if (decimal.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                        numeric.Add(number);
                }
                if (quantity <= 0 && numeric.Count > 0) quantity = Math.Max(1, DecimalToInt(numeric[0], 1));
                if (quantity == 1 && header == null && numeric.Count > 0) quantity = Math.Max(1, DecimalToInt(numeric[0], 1));
                if (header == null && numeric.Count >= 3) period = DecimalToInt(numeric[numeric.Count - 2], 0);
                if (probability <= 0m && numeric.Count > 0) probability = numeric[numeric.Count - 1];
                if (probability <= 0m) probability = 100m;

                int seq;
                if (!sequence.TryGetValue(group, out seq)) seq = 0;
                sequence[group] = seq + 1;

                result.Add(new RewardEntry
                {
                    GroupId = group.Trim(),
                    Sequence = seq,
                    RewardType = string.IsNullOrWhiteSpace(type) ? "SkidItem" : type.Trim(),
                    RewardCode = code == null ? null : code.Trim(),
                    Quantity = Math.Max(1, quantity),
                    Period = Math.Max(0, period),
                    Probability = probability,
                    SourceRow = rowIndex,
                    RawRow = string.Join("\t", row.Select(x => x ?? string.Empty).ToArray())
                });
            }
            return result;
        }

        private static int FindRewardHeader(List<string[]> rows)
        {
            for (var i = 0; i < Math.Min(rows.Count, 30); i++)
            {
                var normalized = rows[i].Select(NormalizeHeader).ToArray();
                if (normalized.Any(x => x.Contains("rewardgroup")) ||
                    (normalized.Any(x => x == "group") && normalized.Any(x => x.Contains("prob"))))
                    return i;
            }
            return -1;
        }

        private static List<VisualBoxMap> ParseVisualBoxMappings(List<string[]> rows, IDictionary<string, UseItemMap> useMaps)
        {
            var result = new List<VisualBoxMap>();
            var headerIndex = -1;
            Dictionary<string, int> header = null;
            for (var i = 0; i < rows.Count; i++)
            {
                var map = BuildHeaderMap(rows[i]);
                if (map.ContainsKey("categoryindex") && map.ContainsKey("itemid") && map.ContainsKey("id"))
                {
                    headerIndex = i;
                    header = map;
                    break;
                }
            }
            if (headerIndex < 0) return result;

            for (var i = headerIndex + 1; i < rows.Count; i++)
            {
                var row = rows[i];
                int shopId;
                if (!int.TryParse(HeaderValue(row, header, "id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out shopId)) continue;
                var visualCode = HeaderValue(row, header, "itemid");
                var param = FirstHeaderValue(row, header, "param", "parameter", "data");
                if (string.IsNullOrWhiteSpace(param)) continue;

                var parts = param.Trim().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var useCode = parts[0].Trim().Trim('"');
                UseItemMap useMap;
                if (!useMaps.TryGetValue(useCode, out useMap)) continue;

                var quantity = 1;
                if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity);
                if (quantity <= 0) quantity = 1;

                result.Add(new VisualBoxMap
                {
                    ShopId = shopId,
                    VisualItemCode = visualCode,
                    UseItemCode = useCode,
                    Quantity = quantity,
                    RewardGroupId = useMap.RewardGroupId,
                    Param = param,
                    SourceRow = i
                });
            }
            return result.GroupBy(x => x.ShopId).Select(x => x.First()).ToList();
        }

        private static bool IsRewardType(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            return text.Equals("SkidItem", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Mito", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Hancoin", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Mileage", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Car", StringComparison.OrdinalIgnoreCase) ||
                   text.Equals("Vehicle", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRewardCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            if (text.StartsWith("rw_", StringComparison.OrdinalIgnoreCase)) return false;
            return text.StartsWith("i_", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("pc_", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("v_", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("car_", StringComparison.OrdinalIgnoreCase);
        }

        private static void InsertSourceCells(SqlConnection connection, SqlTransaction tx, string sourceFile, List<string[]> rows)
        {
            var table = new DataTable();
            table.Columns.Add("SourceFile", typeof(string));
            table.Columns.Add("RowIndex", typeof(int));
            table.Columns.Add("ColumnIndex", typeof(int));
            table.Columns.Add("CellValue", typeof(string));
            for (var r = 0; r < rows.Count; r++)
            {
                var row = rows[r] ?? new string[0];
                for (var c = 0; c < row.Length; c++)
                    table.Rows.Add(sourceFile, r, c, string.IsNullOrEmpty(row[c]) ? (object)DBNull.Value : row[c]);
            }
            BulkCopy(connection, tx, "dbo.reward_source_cells", table);
        }

        private static void InsertManifest(SqlConnection connection, SqlTransaction tx, string sourceFile, int rowCount)
        {
            using (var command = new SqlCommand(
                "INSERT INTO dbo.reward_import_manifest(SourceFile,RowCount,ImportedAt) VALUES(@file,@rows,SYSUTCDATETIME());", connection, tx))
            {
                command.Parameters.AddWithValue("@file", sourceFile);
                command.Parameters.AddWithValue("@rows", rowCount);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertUseItemMappings(SqlConnection connection, SqlTransaction tx, List<UseItemMap> rows)
        {
            var table = new DataTable();
            table.Columns.Add("ItemCode", typeof(string));
            table.Columns.Add("FunctionName", typeof(string));
            table.Columns.Add("RewardGroupId", typeof(string));
            table.Columns.Add("SourceRowIndex", typeof(int));
            foreach (var row in rows) table.Rows.Add(row.ItemCode, Db(row.FunctionName), row.RewardGroupId, row.SourceRow);
            BulkCopy(connection, tx, "dbo.reward_use_item_map", table);
        }

        private static void InsertRewardEntries(SqlConnection connection, SqlTransaction tx, List<RewardEntry> rows)
        {
            var table = new DataTable();
            table.Columns.Add("RewardGroupId", typeof(string));
            table.Columns.Add("Sequence", typeof(int));
            table.Columns.Add("RewardType", typeof(string));
            table.Columns.Add("RewardCode", typeof(string));
            table.Columns.Add("Quantity", typeof(int));
            table.Columns.Add("Period", typeof(int));
            table.Columns.Add("Probability", typeof(decimal));
            table.Columns.Add("SourceRowIndex", typeof(int));
            table.Columns.Add("RawRow", typeof(string));
            foreach (var row in rows)
                table.Rows.Add(row.GroupId, row.Sequence, Db(row.RewardType), Db(row.RewardCode), row.Quantity, row.Period, row.Probability, row.SourceRow, Db(row.RawRow));
            BulkCopy(connection, tx, "dbo.reward_group_items", table);
        }

        private static void InsertVisualMappings(SqlConnection connection, SqlTransaction tx, List<VisualBoxMap> rows)
        {
            var table = new DataTable();
            table.Columns.Add("ShopId", typeof(int));
            table.Columns.Add("VisualItemCode", typeof(string));
            table.Columns.Add("UseItemCode", typeof(string));
            table.Columns.Add("Quantity", typeof(int));
            table.Columns.Add("RewardGroupId", typeof(string));
            table.Columns.Add("Param", typeof(string));
            table.Columns.Add("SourceRowIndex", typeof(int));
            foreach (var row in rows)
                table.Rows.Add(row.ShopId, Db(row.VisualItemCode), row.UseItemCode, row.Quantity, row.RewardGroupId, Db(row.Param), row.SourceRow);
            BulkCopy(connection, tx, "dbo.visual_box_map", table);
        }

        private static void BulkCopy(SqlConnection connection, SqlTransaction tx, string destination, DataTable table)
        {
            if (table.Rows.Count == 0) return;
            using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, tx))
            {
                bulk.DestinationTableName = destination;
                bulk.BatchSize = 2000;
                bulk.BulkCopyTimeout = 300;
                foreach (DataColumn column in table.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                bulk.WriteToServer(table);
            }
        }

        private static object Db(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        }

        private static List<string[]> ReadXlt(string path)
        {
            var bytes = File.ReadAllBytes(path);
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else
                text = Encoding.UTF8.GetString(bytes);
            return ParseQuotedTsv(text);
        }

        private static List<string[]> ParseQuotedTsv(string text)
        {
            var result = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else quoted = !quoted;
                    continue;
                }
                if (!quoted && ch == '\t')
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    continue;
                }
                if (!quoted && (ch == '\r' || ch == '\n'))
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString());
                    cell.Clear();
                    if (row.Any(x => !string.IsNullOrEmpty(x))) result.Add(row.ToArray());
                    row.Clear();
                    continue;
                }
                cell.Append(ch);
            }
            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                if (row.Any(x => !string.IsNullOrEmpty(x))) result.Add(row.ToArray());
            }
            return result;
        }

        private static TdfTable ReadTdf(string path)
        {
            var file = File.ReadAllBytes(path);
            if (file.Length < 4) throw new InvalidDataException("UseItemClient.tdf is too small.");
            var headerBytes = BitConverter.ToUInt16(file, 2);
            if (headerBytes >= file.Length || file.Length - headerBytes < 24)
                throw new InvalidDataException("UseItemClient.tdf has an invalid header offset.");
            var data = new byte[file.Length - headerBytes];
            Buffer.BlockCopy(file, headerBytes, data, 0, data.Length);
            var columns = checked((int)BitConverter.ToUInt32(data, 16));
            var rows = checked((int)BitConverter.ToUInt32(data, 20));
            if (24L + ((long)columns * rows * 4L) > data.Length)
                throw new InvalidDataException("UseItemClient.tdf has a truncated offset table.");

            var result = new TdfTable();
            var cursor = 24;
            for (var r = 0; r < rows; r++)
            {
                var values = new string[columns];
                for (var c = 0; c < columns; c++)
                {
                    var offset = BitConverter.ToUInt32(data, cursor);
                    cursor += 4;
                    values[c] = ReadUnicodeString(data, offset);
                }
                result.Rows.Add(values);
            }
            return result;
        }

        private static string ReadUnicodeString(byte[] data, uint offset)
        {
            if (offset >= data.Length) return string.Empty;
            var start = checked((int)offset);
            var end = start;
            while (end + 1 < data.Length)
            {
                if (data[end] == 0 && data[end + 1] == 0) break;
                end += 2;
            }
            return end > start ? Encoding.Unicode.GetString(data, start, end - start) : string.Empty;
        }

        private static Dictionary<string, int> BuildHeaderMap(string[] row)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (row == null) return map;
            for (var i = 0; i < row.Length; i++)
            {
                var key = NormalizeHeader(row[i]);
                if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key)) map.Add(key, i);
            }
            return map;
        }

        private static string NormalizeHeader(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var b = new StringBuilder();
            foreach (var ch in value.Trim().ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) b.Append(ch);
            return b.ToString();
        }

        private static string HeaderValue(string[] row, IDictionary<string, int> header, string name)
        {
            int index;
            if (header == null || !header.TryGetValue(NormalizeHeader(name), out index)) return string.Empty;
            return Cell(row, index).Trim();
        }

        private static string FirstHeaderValue(string[] row, IDictionary<string, int> header, params string[] names)
        {
            foreach (var name in names)
            {
                var value = HeaderValue(row, header, name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return string.Empty;
        }

        private static string Cell(string[] row, int index)
        {
            return row != null && index >= 0 && index < row.Length ? row[index] ?? string.Empty : string.Empty;
        }

        private static int ParseInt(string value, int fallback)
        {
            int result;
            return int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static decimal ParseDecimal(string value, decimal fallback)
        {
            decimal result;
            return decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static int DecimalToInt(decimal value, int fallback)
        {
            try { return decimal.ToInt32(decimal.Truncate(value)); }
            catch { return fallback; }
        }
    }
}
