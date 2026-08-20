using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace ServerManager
{
    internal static class ItemCatalogImporter
    {
        internal sealed class ImportResult
        {
            public int Count;
            public string JsonPath;
        }

        private sealed class CatalogRoot
        {
            public string generatedAtUtc { get; set; }
            public int count { get; set; }
            public List<CatalogItem> items { get; set; }
        }

        private sealed class CatalogItem
        {
            public int tableIndex { get; set; }
            public string sourceType { get; set; }
            public string id { get; set; }
            public string name { get; set; }
            public string category { get; set; }
            public string description { get; set; }
            public string function { get; set; }
            public string nextState { get; set; }
            public string buyValue { get; set; }
            public string sellValue { get; set; }
            public int? buyPrice { get; set; }
            public int? sellPrice { get; set; }
            public string expirationTime { get; set; }
            public bool? auctionable { get; set; }
            public bool? partsShop { get; set; }
            public bool? sendable { get; set; }
            public bool stackable { get; set; }
            public int? maxStack { get; set; }
            public string grade { get; set; }
            public string requiredLevel { get; set; }
            public string basePoints { get; set; }
            public string basePointModifier { get; set; }
            public string basePointVariable { get; set; }
            public string partAssist { get; set; }
            public string lube { get; set; }
            public string neoStats { get; set; }
            public string stat { get; set; }
            public string cooldown { get; set; }
            public string duration { get; set; }
        }

        public static string CatalogPath
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Catalogs", "ItemCatalog.json");
            }
        }

        public static bool CatalogExists()
        {
            return File.Exists(CatalogPath);
        }

        public static ImportResult Import()
        {
            var path = CatalogPath;
            if (!File.Exists(path))
                throw new FileNotFoundException("ItemCatalog.json was not found. Start Game Server once to generate it, then stop Game Server before importing.", path);

            var json = File.ReadAllText(path);
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
            var catalog = serializer.Deserialize<CatalogRoot>(json);
            if (catalog == null || catalog.items == null || catalog.items.Count == 0)
                throw new InvalidDataException("ItemCatalog.json is empty or invalid.");

            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = "DCServer",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity Server Manager"
            }.ConnectionString;

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                EnsureTable(connection);

                using (var tx = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var item in catalog.items)
                            Upsert(connection, tx, item);

                        // Defensive migration for catalogs imported by older server builds.
                        // Car keys are unique ownership tokens and must never inherit the
                        // source XML's overloaded maxstack value.
                        using (var normalize = new SqlCommand(@"
UPDATE dbo.item_catalog
SET MaxStack=1,
    Stackable=0,
    SourceUpdatedAt=SYSUTCDATETIME()
WHERE LOWER(LTRIM(RTRIM(ISNULL(Category,''))))='car'
  AND LOWER(RTRIM(ISNULL(Name,''))) LIKE '%key';", connection, tx))
                        {
                            normalize.ExecuteNonQuery();
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

            return new ImportResult { Count = catalog.items.Count, JsonPath = path };
        }

        private static void EnsureTable(SqlConnection connection)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.item_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.item_catalog
    (
        TableIndex INT NOT NULL CONSTRAINT PK_item_catalog PRIMARY KEY,
        ItemId VARCHAR(64) NOT NULL,
        SourceType VARCHAR(16) NOT NULL,
        Name NVARCHAR(255) NULL,
        Description NVARCHAR(MAX) NULL,
        Category VARCHAR(64) NULL,
        FunctionName VARCHAR(128) NULL,
        NextState VARCHAR(128) NULL,
        SourceBuyValue VARCHAR(32) NULL,
        SourceSellValue VARCHAR(32) NULL,
        SourceBuyPrice INT NULL,
        SourceSellPrice INT NULL,
        ExpirationTime VARCHAR(32) NULL,
        Auctionable BIT NULL,
        PartsShop BIT NULL,
        Sendable BIT NULL,
        Stackable BIT NOT NULL CONSTRAINT DF_item_catalog_Stackable DEFAULT (0),
        MaxStack INT NULL,
        Grade VARCHAR(16) NULL,
        RequiredLevel INT NULL,
        BasePoints INT NULL,
        BasePointModifier INT NULL,
        BasePointVariable INT NULL,
        PartAssist VARCHAR(64) NULL,
        Lube VARCHAR(64) NULL,
        NeoStats VARCHAR(128) NULL,
        StatModifier VARCHAR(64) NULL,
        Cooldown VARCHAR(64) NULL,
        Duration VARCHAR(64) NULL,
        IsEnabled BIT NOT NULL CONSTRAINT DF_item_catalog_IsEnabled DEFAULT (1),
        ServerBuyPrice INT NULL,
        ServerSellPrice INT NULL,
        SourceUpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_item_catalog_SourceUpdatedAt DEFAULT (SYSUTCDATETIME()),
        AdminUpdatedAt DATETIME2 NULL
    );
    CREATE UNIQUE INDEX UX_item_catalog_ItemId ON dbo.item_catalog(ItemId);
    CREATE INDEX IX_item_catalog_Category ON dbo.item_catalog(Category);
    CREATE INDEX IX_item_catalog_Name ON dbo.item_catalog(Name);
END;";

            using (var cmd = new SqlCommand(sql, connection))
                cmd.ExecuteNonQuery();
        }

        private static void Upsert(SqlConnection connection, SqlTransaction tx, CatalogItem item)
        {
            var vehicleKey = IsVehicleKey(item);
            var stackable = vehicleKey ? false : item.stackable;
            int? maxStack = vehicleKey ? 1 : item.maxStack;

            const string sql = @"
MERGE dbo.item_catalog AS target
USING (SELECT @TableIndex AS TableIndex) AS source
ON target.TableIndex = source.TableIndex
WHEN MATCHED THEN
    UPDATE SET
        ItemId=@ItemId, SourceType=@SourceType, Name=@Name, Description=@Description,
        Category=@Category, FunctionName=@FunctionName, NextState=@NextState,
        SourceBuyValue=@SourceBuyValue, SourceSellValue=@SourceSellValue,
        SourceBuyPrice=@SourceBuyPrice, SourceSellPrice=@SourceSellPrice,
        ExpirationTime=@ExpirationTime, Auctionable=@Auctionable, PartsShop=@PartsShop,
        Sendable=@Sendable, Stackable=@Stackable, MaxStack=@MaxStack, Grade=@Grade,
        RequiredLevel=@RequiredLevel, BasePoints=@BasePoints,
        BasePointModifier=@BasePointModifier, BasePointVariable=@BasePointVariable,
        PartAssist=@PartAssist, Lube=@Lube, NeoStats=@NeoStats,
        StatModifier=@StatModifier, Cooldown=@Cooldown, Duration=@Duration,
        SourceUpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (TableIndex,ItemId,SourceType,Name,Description,Category,FunctionName,NextState,
            SourceBuyValue,SourceSellValue,SourceBuyPrice,SourceSellPrice,ExpirationTime,
            Auctionable,PartsShop,Sendable,Stackable,MaxStack,Grade,RequiredLevel,
            BasePoints,BasePointModifier,BasePointVariable,PartAssist,Lube,NeoStats,
            StatModifier,Cooldown,Duration,IsEnabled,SourceUpdatedAt)
    VALUES (@TableIndex,@ItemId,@SourceType,@Name,@Description,@Category,@FunctionName,@NextState,
            @SourceBuyValue,@SourceSellValue,@SourceBuyPrice,@SourceSellPrice,@ExpirationTime,
            @Auctionable,@PartsShop,@Sendable,@Stackable,@MaxStack,@Grade,@RequiredLevel,
            @BasePoints,@BasePointModifier,@BasePointVariable,@PartAssist,@Lube,@NeoStats,
            @StatModifier,@Cooldown,@Duration,1,SYSUTCDATETIME());";

            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@TableIndex", item.tableIndex);
                cmd.Parameters.AddWithValue("@ItemId", Db(item.id));
                cmd.Parameters.AddWithValue("@SourceType", Db(item.sourceType));
                cmd.Parameters.AddWithValue("@Name", Db(item.name));
                cmd.Parameters.AddWithValue("@Description", Db(item.description));
                cmd.Parameters.AddWithValue("@Category", Db(item.category));
                cmd.Parameters.AddWithValue("@FunctionName", Db(item.function));
                cmd.Parameters.AddWithValue("@NextState", Db(item.nextState));
                cmd.Parameters.AddWithValue("@SourceBuyValue", Db(item.buyValue));
                cmd.Parameters.AddWithValue("@SourceSellValue", Db(item.sellValue));
                cmd.Parameters.AddWithValue("@SourceBuyPrice", Db(item.buyPrice));
                cmd.Parameters.AddWithValue("@SourceSellPrice", Db(item.sellPrice));
                cmd.Parameters.AddWithValue("@ExpirationTime", Db(item.expirationTime));
                cmd.Parameters.AddWithValue("@Auctionable", Db(item.auctionable));
                cmd.Parameters.AddWithValue("@PartsShop", Db(item.partsShop));
                cmd.Parameters.AddWithValue("@Sendable", Db(item.sendable));
                cmd.Parameters.AddWithValue("@Stackable", stackable);
                cmd.Parameters.AddWithValue("@MaxStack", Db(maxStack));
                cmd.Parameters.AddWithValue("@Grade", Db(item.grade));
                cmd.Parameters.AddWithValue("@RequiredLevel", DbInt(item.requiredLevel));
                cmd.Parameters.AddWithValue("@BasePoints", DbInt(item.basePoints));
                cmd.Parameters.AddWithValue("@BasePointModifier", DbInt(item.basePointModifier));
                cmd.Parameters.AddWithValue("@BasePointVariable", DbInt(item.basePointVariable));
                cmd.Parameters.AddWithValue("@PartAssist", Db(item.partAssist));
                cmd.Parameters.AddWithValue("@Lube", Db(item.lube));
                cmd.Parameters.AddWithValue("@NeoStats", Db(item.neoStats));
                cmd.Parameters.AddWithValue("@StatModifier", Db(item.stat));
                cmd.Parameters.AddWithValue("@Cooldown", Db(item.cooldown));
                cmd.Parameters.AddWithValue("@Duration", Db(item.duration));
                cmd.ExecuteNonQuery();
            }
        }

        private static bool IsVehicleKey(CatalogItem item)
        {
            if (item == null) return false;
            if (!string.Equals((item.category ?? string.Empty).Trim(), "car", StringComparison.OrdinalIgnoreCase))
                return false;
            return (item.name ?? string.Empty).Trim().EndsWith("key", StringComparison.OrdinalIgnoreCase);
        }

        private static object Db(object value)
        {
            return value ?? DBNull.Value;
        }

        private static object DbInt(string raw)
        {
            int value;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? (object)value
                : DBNull.Value;
        }
    }
}
