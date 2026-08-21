using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerManager
{
    internal static class VShopDataImporter
    {
        private static readonly string[] RequiredFileNames =
        {
            "VShopItem.xlt",
            "VisualItem.xlt"
        };

        internal sealed class ImportResult
        {
            public int Files;
            public int Rows;
            public int VisualMatches;
            public int MissingVisualMatches;
            public string Folder;
        }

        private sealed class VisualDefinition
        {
            public int CategoryIndex;
            public string ItemCode;
            public string Name;
            public string Param;
        }

        public static string ImportFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Importer"); }
        }

        public static string EnsureImportDirectory()
        {
            Directory.CreateDirectory(ImportFolder);
            return ImportFolder;
        }

        public static string[] GetMissingRequiredFiles()
        {
            var folder = EnsureImportDirectory();
            return RequiredFileNames.Where(name => !File.Exists(Path.Combine(folder, name))).ToArray();
        }

        public static ImportResult ImportAll()
        {
            var folder = EnsureImportDirectory();
            var missing = GetMissingRequiredFiles();
            if (missing.Length != 0)
                throw new InvalidOperationException("Missing required VShop XLT files: " + string.Join(", ", missing));

            var vshopPath = Path.Combine(folder, "VShopItem.xlt");
            var visualPath = Path.Combine(folder, "VisualItem.xlt");
            var visualDefinitions = LoadVisualDefinitions(visualPath);
            var rows = ReadTable(vshopPath, "Index\tSupport\tUniqueId\t");

            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = "DCServer",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity VShop XLT Importer"
            }.ConnectionString;

            var imported = 0;
            var matched = 0;
            var missingVisual = 0;

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var tx = connection.BeginTransaction())
                {
                    try
                    {
                        EnsureSchema(connection, tx);

                        foreach (var row in rows)
                        {
                            int shopId;
                            if (!TryInt(Get(row, "UniqueId"), out shopId))
                                continue;

                            VisualDefinition visual;
                            visualDefinitions.TryGetValue(shopId, out visual);
                            if (visual == null) missingVisual++;
                            else matched++;

                            Upsert(connection, tx, row, visual, shopId, vshopPath);
                            imported++;
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

            return new ImportResult
            {
                Files = 2,
                Rows = imported,
                VisualMatches = matched,
                MissingVisualMatches = missingVisual,
                Folder = folder
            };
        }

        private static Dictionary<int, VisualDefinition> LoadVisualDefinitions(string path)
        {
            var result = new Dictionary<int, VisualDefinition>();
            foreach (var row in ReadTable(path, "Category\tcategory index\tindex\titem_id\tid\t"))
            {
                int id;
                if (!TryInt(Get(row, "id"), out id)) continue;

                int categoryIndex;
                TryInt(Get(row, "category index"), out categoryIndex);
                result[id] = new VisualDefinition
                {
                    CategoryIndex = categoryIndex,
                    ItemCode = Get(row, "item_id"),
                    Name = Get(row, "Name"),
                    Param = Get(row, "Param")
                };
            }
            return result;
        }

        private static List<Dictionary<string, string>> ReadTable(string path, string headerPrefix)
        {
            var bytes = File.ReadAllBytes(path);
            string text;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else
                text = Encoding.Unicode.GetString(bytes);

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var headerLine = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(headerPrefix, StringComparison.Ordinal))
                {
                    headerLine = i;
                    break;
                }
            }

            if (headerLine < 0)
                throw new InvalidDataException("Could not find the expected XLT header in " + Path.GetFileName(path) + ".");

            var headers = lines[headerLine].Split('\t');
            var result = new List<Dictionary<string, string>>();
            for (var i = headerLine + 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var values = lines[i].Split('\t');
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < headers.Length; c++)
                {
                    var name = (headers[c] ?? string.Empty).Trim();
                    if (name.Length == 0 || row.ContainsKey(name)) continue;
                    row[name] = c < values.Length ? (values[c] ?? string.Empty).Trim().Trim('"') : string.Empty;
                }
                result.Add(row);
            }
            return result;
        }

        private static void EnsureSchema(SqlConnection connection, SqlTransaction tx)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.visual_item_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.visual_item_catalog
    (
        ShopId INT NOT NULL CONSTRAINT PK_visual_item_catalog PRIMARY KEY,
        ItemCode NVARCHAR(64) NULL,
        Category NVARCHAR(64) NULL,
        CategoryIndex INT NOT NULL CONSTRAINT DF_visual_item_catalog_CategoryIndex DEFAULT (0),
        Support INT NOT NULL CONSTRAINT DF_visual_item_catalog_Support DEFAULT (0),
        UseMito BIT NOT NULL CONSTRAINT DF_visual_item_catalog_UseMito DEFAULT (0),
        UseHancoin BIT NOT NULL CONSTRAINT DF_visual_item_catalog_UseHancoin DEFAULT (0),
        UseMileage BIT NOT NULL CONSTRAINT DF_visual_item_catalog_UseMileage DEFAULT (0),
        SourceMitoPrice INT NULL, SourceMito7dPrice INT NULL, SourceMito30dPrice INT NULL,
        SourceMito90dPrice INT NULL, SourceMito365dPrice INT NULL, SourceMito0dPrice INT NULL,
        SourceHancoin7dPrice INT NULL, SourceHancoin30dPrice INT NULL, SourceHancoin90dPrice INT NULL,
        SourceHancoin365dPrice INT NULL, SourceHancoin0dPrice INT NULL,
        SourceMileage7dPrice INT NULL, SourceMileage30dPrice INT NULL, SourceMileage90dPrice INT NULL,
        SourceMileage365dPrice INT NULL, SourceMileage0dPrice INT NULL,
        ServerMitoPrice INT NULL, ServerMito7dPrice INT NULL, ServerMito30dPrice INT NULL,
        ServerMito90dPrice INT NULL, ServerMito365dPrice INT NULL, ServerMito0dPrice INT NULL,
        ServerHancoin7dPrice INT NULL, ServerHancoin30dPrice INT NULL, ServerHancoin90dPrice INT NULL,
        ServerHancoin365dPrice INT NULL, ServerHancoin0dPrice INT NULL,
        ServerMileage7dPrice INT NULL, ServerMileage30dPrice INT NULL, ServerMileage90dPrice INT NULL,
        ServerMileage365dPrice INT NULL, ServerMileage0dPrice INT NULL,
        SourceBonusMito7d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito7 DEFAULT (0),
        SourceBonusMito30d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito30 DEFAULT (0),
        SourceBonusMito90d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito90 DEFAULT (0),
        SourceBonusMito365d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito365 DEFAULT (0),
        SourceBonusMito0d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito0 DEFAULT (0),
        SourceBonusSpeed INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusSpeed DEFAULT (0),
        SourceBonusCrash INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusCrash DEFAULT (0),
        SourceBonusAccel INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusAccel DEFAULT (0),
        SourceBonusBoost INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusBoost DEFAULT (0),
        SourceBonusAssist INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusAssist DEFAULT (0),
        ServerBonusSpeed INT NULL, ServerBonusCrash INT NULL, ServerBonusAccel INT NULL,
        ServerBonusBoost INT NULL, ServerBonusAssist INT NULL,
        UpdatedUtc DATETIME2 NOT NULL CONSTRAINT DF_visual_item_catalog_UpdatedUtc DEFAULT (SYSUTCDATETIME())
    );
END;

IF COL_LENGTH('dbo.visual_item_catalog','ClientRowIndex') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ClientRowIndex INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','DisplayName') IS NULL ALTER TABLE dbo.visual_item_catalog ADD DisplayName NVARCHAR(128) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','Description') IS NULL ALTER TABLE dbo.visual_item_catalog ADD Description NVARCHAR(1000) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','TopCategory') IS NULL ALTER TABLE dbo.visual_item_catalog ADD TopCategory NVARCHAR(64) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','MainCategoryId') IS NULL ALTER TABLE dbo.visual_item_catalog ADD MainCategoryId INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','MainCategory') IS NULL ALTER TABLE dbo.visual_item_catalog ADD MainCategory NVARCHAR(64) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SubCategoryId') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SubCategoryId INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SubCategory') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SubCategory NVARCHAR(64) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SubCategoryName') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SubCategoryName NVARCHAR(128) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','VisualItemReference') IS NULL ALTER TABLE dbo.visual_item_catalog ADD VisualItemReference INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','VisualParam') IS NULL ALTER TABLE dbo.visual_item_catalog ADD VisualParam NVARCHAR(128) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','UnEquipable') IS NULL ALTER TABLE dbo.visual_item_catalog ADD UnEquipable BIT NOT NULL CONSTRAINT DF_vcatalog_UnEquipable DEFAULT(0);
IF COL_LENGTH('dbo.visual_item_catalog','IsNew') IS NULL ALTER TABLE dbo.visual_item_catalog ADD IsNew BIT NOT NULL CONSTRAINT DF_vcatalog_IsNew DEFAULT(0);
IF COL_LENGTH('dbo.visual_item_catalog','IsHot') IS NULL ALTER TABLE dbo.visual_item_catalog ADD IsHot BIT NOT NULL CONSTRAINT DF_vcatalog_IsHot DEFAULT(0);
IF COL_LENGTH('dbo.visual_item_catalog','SellByCarType') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SellByCarType INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','MagicNumber') IS NULL ALTER TABLE dbo.visual_item_catalog ADD MagicNumber BIGINT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ImportedFrom') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ImportedFrom NVARCHAR(260) NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ImportedUtc') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ImportedUtc DATETIME2 NULL;";

            using (var cmd = new SqlCommand(sql, connection, tx)) cmd.ExecuteNonQuery();
        }

        private static void Upsert(SqlConnection connection, SqlTransaction tx, Dictionary<string, string> row,
            VisualDefinition visual, int shopId, string sourcePath)
        {
            const string sql = @"
MERGE dbo.visual_item_catalog AS target
USING (SELECT @ShopId AS ShopId) AS src ON target.ShopId=src.ShopId
WHEN MATCHED THEN UPDATE SET
 ItemCode=@ItemCode, Category=@Category, CategoryIndex=@CategoryIndex, Support=@Support,
 UseMito=@UseMito, UseHancoin=@UseHancoin, UseMileage=@UseMileage,
 SourceMitoPrice=@Mito, SourceMito7dPrice=@Mito7, SourceMito30dPrice=@Mito30,
 SourceMito90dPrice=@Mito90, SourceMito365dPrice=@Mito365, SourceMito0dPrice=@Mito0,
 SourceHancoin7dPrice=@Hc7, SourceHancoin30dPrice=@Hc30, SourceHancoin90dPrice=@Hc90,
 SourceHancoin365dPrice=@Hc365, SourceHancoin0dPrice=@Hc0,
 SourceMileage7dPrice=@Mi7, SourceMileage30dPrice=@Mi30, SourceMileage90dPrice=@Mi90,
 SourceMileage365dPrice=@Mi365, SourceMileage0dPrice=@Mi0,
 SourceBonusMito7d=@Bm7, SourceBonusMito30d=@Bm30, SourceBonusMito90d=@Bm90,
 SourceBonusMito365d=@Bm365, SourceBonusMito0d=@Bm0,
 SourceBonusSpeed=@Bs, SourceBonusAccel=@Ba, SourceBonusBoost=@Bb, SourceBonusCrash=@Bc, SourceBonusAssist=@Bassist,
 ClientRowIndex=@ClientRowIndex, DisplayName=@DisplayName, Description=@Description,
 TopCategory=@TopCategory, MainCategoryId=@MainCategoryId, MainCategory=@MainCategory,
 SubCategoryId=@SubCategoryId, SubCategory=@SubCategory, SubCategoryName=@SubCategoryName,
 VisualItemReference=@VisualRef, VisualParam=@VisualParam, UnEquipable=@UnEquipable,
 IsNew=@IsNew, IsHot=@IsHot, SellByCarType=@SellByCarType, MagicNumber=@MagicNumber,
 ImportedFrom=@ImportedFrom, ImportedUtc=SYSUTCDATETIME(), UpdatedUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
(ShopId,ItemCode,Category,CategoryIndex,Support,UseMito,UseHancoin,UseMileage,
 SourceMitoPrice,SourceMito7dPrice,SourceMito30dPrice,SourceMito90dPrice,SourceMito365dPrice,SourceMito0dPrice,
 SourceHancoin7dPrice,SourceHancoin30dPrice,SourceHancoin90dPrice,SourceHancoin365dPrice,SourceHancoin0dPrice,
 SourceMileage7dPrice,SourceMileage30dPrice,SourceMileage90dPrice,SourceMileage365dPrice,SourceMileage0dPrice,
 SourceBonusMito7d,SourceBonusMito30d,SourceBonusMito90d,SourceBonusMito365d,SourceBonusMito0d,
 SourceBonusSpeed,SourceBonusAccel,SourceBonusBoost,SourceBonusCrash,SourceBonusAssist,
 ClientRowIndex,DisplayName,Description,TopCategory,MainCategoryId,MainCategory,SubCategoryId,SubCategory,SubCategoryName,
 VisualItemReference,VisualParam,UnEquipable,IsNew,IsHot,SellByCarType,MagicNumber,ImportedFrom,ImportedUtc)
VALUES
(@ShopId,@ItemCode,@Category,@CategoryIndex,@Support,@UseMito,@UseHancoin,@UseMileage,
 @Mito,@Mito7,@Mito30,@Mito90,@Mito365,@Mito0,@Hc7,@Hc30,@Hc90,@Hc365,@Hc0,
 @Mi7,@Mi30,@Mi90,@Mi365,@Mi0,@Bm7,@Bm30,@Bm90,@Bm365,@Bm0,
 @Bs,@Ba,@Bb,@Bc,@Bassist,@ClientRowIndex,@DisplayName,@Description,@TopCategory,@MainCategoryId,@MainCategory,
 @SubCategoryId,@SubCategory,@SubCategoryName,@VisualRef,@VisualParam,@UnEquipable,@IsNew,@IsHot,@SellByCarType,
 @MagicNumber,@ImportedFrom,SYSUTCDATETIME());";

            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                Add(cmd, "@ShopId", shopId);
                Add(cmd, "@ItemCode", visual != null && !string.IsNullOrWhiteSpace(visual.ItemCode) ? visual.ItemCode : Get(row, "ItemName"));
                Add(cmd, "@Category", Get(row, "Category"));
                Add(cmd, "@CategoryIndex", visual == null ? 0 : visual.CategoryIndex);
                Add(cmd, "@Support", IntValue(row, "Support"));
                Add(cmd, "@UseMito", BoolValue(row, "Use Mito"));
                Add(cmd, "@UseHancoin", BoolValue(row, "UseHancoin"));
                Add(cmd, "@UseMileage", BoolValue(row, "Use Mileage"));
                Add(cmd, "@Mito", NullableInt(row, "Mito Price"));
                Add(cmd, "@Mito7", NullableInt(row, "Mito Price7D"));
                Add(cmd, "@Mito30", NullableInt(row, "Mito Price30D"));
                Add(cmd, "@Mito90", NullableInt(row, "Mito Price90D"));
                Add(cmd, "@Mito365", NullableInt(row, "Mito Price365D"));
                Add(cmd, "@Mito0", NullableInt(row, "Mito Price0D"));
                Add(cmd, "@Hc7", NullableInt(row, "$Price 7D"));
                Add(cmd, "@Hc30", NullableInt(row, "$Price 30D"));
                Add(cmd, "@Hc90", NullableInt(row, "$Price 90D"));
                Add(cmd, "@Hc365", NullableInt(row, "$Price 365D"));
                Add(cmd, "@Hc0", NullableInt(row, "$Price 0D"));
                Add(cmd, "@Mi7", NullableInt(row, "Mile Price7D"));
                Add(cmd, "@Mi30", NullableInt(row, "Mile Price30D"));
                Add(cmd, "@Mi90", NullableInt(row, "Mile Price90D"));
                Add(cmd, "@Mi365", NullableInt(row, "Mile Price365D"));
                Add(cmd, "@Mi0", NullableInt(row, "Mile Price0D"));
                Add(cmd, "@Bm7", IntValue(row, "Bonus Mito 7D"));
                Add(cmd, "@Bm30", IntValue(row, "Bonus Mito 30D"));
                Add(cmd, "@Bm90", IntValue(row, "Bonus Mito 90D"));
                Add(cmd, "@Bm365", IntValue(row, "Bonus Mito 365D"));
                Add(cmd, "@Bm0", IntValue(row, "Bonus Mito 0D"));
                Add(cmd, "@Bs", IntValue(row, "Bonus Speed"));
                Add(cmd, "@Ba", IntValue(row, "Bonus Accel"));
                Add(cmd, "@Bb", IntValue(row, "Bonus Boost"));
                Add(cmd, "@Bc", IntValue(row, "Bonus Crash"));
                Add(cmd, "@Bassist", IntValue(row, "Bonus Assist"));
                Add(cmd, "@ClientRowIndex", NullableInt(row, "Index"));
                Add(cmd, "@DisplayName", Get(row, "MarketName"));
                Add(cmd, "@Description", Get(row, "Desc (Tooltip)"));
                Add(cmd, "@TopCategory", Get(row, "Top Category"));
                Add(cmd, "@MainCategoryId", NullableInt(row, "Main"));
                Add(cmd, "@MainCategory", Get(row, "Category"));
                Add(cmd, "@SubCategoryId", NullableInt(row, "Sub"));
                Add(cmd, "@SubCategory", Get(row, "Sub Catergory"));
                Add(cmd, "@SubCategoryName", Get(row, "Name"));
                Add(cmd, "@VisualRef", NullableInt(row, "Refer VisualItem"));
                Add(cmd, "@VisualParam", visual == null ? null : visual.Param);
                Add(cmd, "@UnEquipable", BoolValue(row, "UnEquipable"));
                Add(cmd, "@IsNew", BoolValue(row, "New"));
                Add(cmd, "@IsHot", BoolValue(row, "Hot"));
                Add(cmd, "@SellByCarType", NullableInt(row, "SellByCarType"));
                Add(cmd, "@MagicNumber", NullableLong(row, "MagicNumber"));
                Add(cmd, "@ImportedFrom", sourcePath);
                cmd.ExecuteNonQuery();
            }
        }

        private static void Add(SqlCommand cmd, string name, object value)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        private static string Get(Dictionary<string, string> row, string key)
        {
            string value;
            return row.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static bool TryInt(string value, out int result)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static int IntValue(Dictionary<string, string> row, string key)
        {
            int value;
            return TryInt(Get(row, key), out value) ? value : 0;
        }

        private static bool BoolValue(Dictionary<string, string> row, string key)
        {
            var value = Get(row, key);
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static object NullableInt(Dictionary<string, string> row, string key)
        {
            int value;
            return TryInt(Get(row, key), out value) ? (object)value : DBNull.Value;
        }

        private static object NullableLong(Dictionary<string, string> row, string key)
        {
            long value;
            return long.TryParse(Get(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? (object)value
                : DBNull.Value;
        }
    }
}
