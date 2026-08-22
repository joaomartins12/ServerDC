using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GameServer.Database;
using GameServer.Util;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer
{
    public class GameServer : ServerMain
    {
        public static readonly GameServer Instance = new GameServer();
        public static GameChatCommands ChatCommands = new GameChatCommands();
        private bool _running;

        private GameServer()
        {
        }

        public DefaultServer Server { get; set; }
        public GameDatabase Database { get; private set; }
        public GameConf Config { get; set; }

        public void Run()
        {
            if (_running)
                throw new Exception("Server is already running.");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var executableDirectory = AppDomain.CurrentDomain.BaseDirectory;

            int x, y, width, height;
            Win32.GetWindowPosition(out x, out y, out width, out height);
            Win32.SetWindowPosition(width + 5, 0, width, height);

            ConsoleUtil.WriteHeader($"Game Server ({Shared.Util.Version.GetVersion()})", ConsoleColor.DarkGreen);
            ConsoleUtil.LoadingTitle();

            Log.Info("Server startup requested");
            Log.Info($"Server Version {Shared.Util.Version.GetVersion()}");

            NavigateToRoot();
            LoadConf(Config = new GameConf());
            InitDatabase(Database = new GameDatabase(), Config);

            // Heal the old character-creation regression before any player data is loaded.
            // CharacterModel also performs the same conservative level-one check on login.
            using (var expRepairConnection = Database.Connection)
                CharacterModel.RepairInvalidExperienceRows(expRepairConnection);

            Log.Info("Loading Vehicles..");
            if (File.Exists("system/data/Vehicles.xml"))
            {
                try
                {
                    Vehicles = GameData.LoadVehicleData("system/data/vehicles.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("Vehicle Data corrupt");
#else
                    throw;
#endif
                }
            }

            Log.Info("Loading VShop Items..");
            if (File.Exists("system/data/VShopItems.xml"))
            {
                try
                {
                    VisualItems = GameData.LoadVShopItems("system/data/VShopItems.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("VShop Items corrupt!");
#else
                    throw;
#endif
                }
            }
            else
            {
                throw new FileNotFoundException("VShopItem data not found!");
            }
            Log.Info("VShop Items loaded with {0:D} entries", VisualItems.Count);

            using (var visualShopConnection = Database.Connection)
            {
                VisualShopDatabase.EnsureSchemaAndSynchronize(visualShopConnection, VisualItems);

                // Client XLT files live in an Importer folder next to GameServer.exe.
                // AppDomain.BaseDirectory is captured before NavigateToRoot() changes CWD.
                VShopClientXltImporter.ImportIfPresent(
                    visualShopConnection,
                    Path.Combine(executableDirectory, "Importer", "VShopItem.xlt"),
                    Path.Combine(executableDirectory, "Importer", "VisualItem.xlt"));
            }

            Log.Info("Loading Quest Table");
            if (File.Exists("system/data/Quests.xml"))
            {
                try
                {
                    Quests = GameData.LoadQuests("system/data/Quests.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("Quest data corrupt!");
#else
                    throw;
#endif
                }
            }
            else
            {
                throw new FileNotFoundException("Quest data not found!");
            }
            Log.Info("Quest Table loaded with {0:D} entries", Quests.Count);

            Log.Info("Loading Item Table");
            if (File.Exists("system/data/Items.xml"))
            {
                try
                {
                    Items = GameData.LoadItems("system/data/Items.xml", "system/data/UseItems.xml");
                }
                catch (Exception)
                {
#if !DEBUG
                    throw new Exception("Items data corrupt!");
#else
                    throw;
#endif
                }
            }
            else
            {
                throw new FileNotFoundException("Items data not found!");
            }
            Log.Info("Item Table loaded with {0:D} entries", Items.Count);

            var reader = new TdfReader();
            if (reader.Load("system/data/LevelServer.tdf"))
            {
                Log.Debug("Loading Exp Table");
                LevelTable = XiExpTable.LoadFromTdf(reader);
                if (LevelTable.Count == 0) throw new InvalidDataException("LevelTable corrupt!");
                Log.Debug("Exp Table Initialized with {0:D} rows.", LevelTable.Count);
            }
            else
            {
                Log.Debug("Exp Table Load failed.");
            }

            GameDataCatalogExporter.Export(Items, VisualItems, Vehicles, Quests, LevelTable);
            ItemCatalogJsonExporter.Export(Items);
            VehicleCatalogJsonExporter.Export(Vehicles);
            VehicleKeyResearchExporter.Export(Vehicles, Items);
            VehicleKeyDatabaseExporter.SyncExistingCatalogRows();
            Log.Info("Catalog exports ready. Official vehicle KeyItemId values were synchronized into existing dbo.vehicle_catalog rows.");

            Server = new DefaultServer(Config.Game.Port);
            Server.Start();

            ConsoleUtil.RunningTitle();
            _running = true;

            watch.Stop();
            Log.Info("Ready after {0}ms", watch.ElapsedMilliseconds);

            var commands = new GameConsoleCommands();
            commands.Wait();
        }
    }
}

namespace GameServer.Util
{
    /// <summary>
    /// Imports the retail client's UTF-16 XLT visual-shop tables into SQL Server.
    /// VShopItem.xlt supplies names, categories, periods, prices and stat bonuses.
    /// VisualItem.xlt supplies the real client visual category index used for equip slots.
    /// </summary>
    public static class VShopClientXltImporter
    {
        private sealed class VisualDefinition
        {
            public int CategoryIndex;
            public string ItemCode;
            public string Name;
            public string Param;
        }

        public static void ImportIfPresent(
            Shared.Models.MySqlConnection conn,
            string vshopItemPath,
            string visualItemPath)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));

            if (!File.Exists(vshopItemPath))
            {
                Log.Warning(
                    "Client VShop import skipped: {0} was not found. Converted VShopItems.xml remains the fallback source.",
                    vshopItemPath);
                return;
            }

            if (!File.Exists(visualItemPath))
            {
                Log.Warning(
                    "Client VShop import skipped: {0} was not found. Both XLT files are required for authoritative category indexes.",
                    visualItemPath);
                return;
            }

            VisualShopDatabase.EnsureSchema(conn);
            EnsureReadableColumns(conn);

            var visualDefinitions = LoadVisualDefinitions(visualItemPath);
            var rows = ReadTable(vshopItemPath, "Index\tSupport\tUniqueId\t");
            var imported = 0;
            var missingVisual = 0;

            foreach (var row in rows)
            {
                int shopId;
                if (!TryInt(Get(row, "UniqueId"), out shopId))
                    continue;

                VisualDefinition visual;
                visualDefinitions.TryGetValue(shopId, out visual);
                if (visual == null)
                    missingVisual++;

                UpsertCatalogRow(conn, row, visual, shopId, vshopItemPath);
                imported++;
            }

            VisualShopDatabase.RepairLegacyPeriods(conn);

            Log.Info(
                "Client VShop XLT import complete: {0} rows imported from {1}; {2} rows had no VisualItem.xlt match.",
                imported,
                vshopItemPath,
                missingVisual);
        }

        private static void EnsureReadableColumns(Shared.Models.MySqlConnection conn)
        {
            const string sql = @"
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

            using (var cmd = new Shared.Models.MySqlCommand(sql, conn))
                cmd.ExecuteNonQuery();
        }

        private static Dictionary<int, VisualDefinition> LoadVisualDefinitions(string path)
        {
            var result = new Dictionary<int, VisualDefinition>();
            foreach (var row in ReadTable(path, "Category\tcategory index\tindex\titem_id\tid\t"))
            {
                int id;
                if (!TryInt(Get(row, "id"), out id))
                    continue;

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

        /// <summary>
        /// Reads UTF-16 XLT as a quoted TSV stream. Retail descriptions can contain real
        /// CR/LF characters inside a quoted field (Robo-B Box is one example), therefore
        /// File.ReadAllLines/Split('\t') silently shifted/lost all columns after the break.
        /// </summary>
        private static List<Dictionary<string, string>> ReadTable(string path, string headerPrefix)
        {
            var text = File.ReadAllText(path, Encoding.Unicode);
            var headerStart = text.IndexOf(headerPrefix, StringComparison.Ordinal);
            if (headerStart < 0)
                throw new InvalidDataException("Could not find expected XLT header in " + path);

            var records = ParseQuotedTsv(text.Substring(headerStart));
            if (records.Count == 0)
                throw new InvalidDataException("XLT table is empty in " + path);

            var headers = records[0];
            var result = new List<Dictionary<string, string>>();
            for (var i = 1; i < records.Count; i++)
            {
                var values = records[i];
                if (values.Count == 0 || (values.Count == 1 && string.IsNullOrWhiteSpace(values[0])))
                    continue;

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < headers.Count; c++)
                {
                    var name = (headers[c] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(name) || row.ContainsKey(name))
                        continue;
                    row[name] = c < values.Count ? (values[c] ?? string.Empty).Trim() : string.Empty;
                }
                result.Add(row);
            }
            return result;
        }

        private static List<List<string>> ParseQuotedTsv(string text)
        {
            var records = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                    continue;
                }

                if (ch == '\t' && !quoted)
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                    continue;
                }

                if ((ch == '\r' || ch == '\n') && !quoted)
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString());
                    field.Length = 0;
                    records.Add(row);
                    row = new List<string>();
                    continue;
                }

                if (ch == '\r' && quoted)
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    field.Append('\n');
                    continue;
                }

                field.Append(ch);
            }

            if (field.Length != 0 || row.Count != 0)
            {
                row.Add(field.ToString());
                records.Add(row);
            }
            return records;
        }

        private static void UpsertCatalogRow(
            Shared.Models.MySqlConnection conn,
            Dictionary<string, string> row,
            VisualDefinition visual,
            int shopId,
            string sourcePath)
        {
            const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.visual_item_catalog WHERE ShopId=@shopId)
BEGIN
    UPDATE dbo.visual_item_catalog SET
        ItemCode=@itemCode,
        Category=@category,
        CategoryIndex=@categoryIndex,
        Support=@support,
        UseMito=@useMito,
        UseHancoin=@useHancoin,
        UseMileage=@useMileage,
        SourceMitoPrice=@mito,
        SourceMito7dPrice=@mito7,
        SourceMito30dPrice=@mito30,
        SourceMito90dPrice=@mito90,
        SourceMito365dPrice=@mito365,
        SourceMito0dPrice=@mito0,
        SourceHancoin7dPrice=@hc7,
        SourceHancoin30dPrice=@hc30,
        SourceHancoin90dPrice=@hc90,
        SourceHancoin365dPrice=@hc365,
        SourceHancoin0dPrice=@hc0,
        SourceMileage7dPrice=@mile7,
        SourceMileage30dPrice=@mile30,
        SourceMileage90dPrice=@mile90,
        SourceMileage365dPrice=@mile365,
        SourceMileage0dPrice=@mile0,
        SourcePeriod7d=@period7,
        SourcePeriod30d=@period30,
        SourcePeriod90d=@period90,
        SourcePeriod365d=@period365,
        SourcePeriod0d=@period0,
        SourceBonusMito7d=@bonusMito7,
        SourceBonusMito30d=@bonusMito30,
        SourceBonusMito90d=@bonusMito90,
        SourceBonusMito365d=@bonusMito365,
        SourceBonusMito0d=@bonusMito0,
        SourceBonusSpeed=@bonusSpeed,
        SourceBonusAccel=@bonusAccel,
        SourceBonusBoost=@bonusBoost,
        SourceBonusCrash=@bonusCrash,
        SourceBonusAssist=@bonusAssist,
        ClientRowIndex=@clientRowIndex,
        DisplayName=@displayName,
        Description=@description,
        TopCategory=@topCategory,
        MainCategoryId=@mainCategoryId,
        MainCategory=@mainCategory,
        SubCategoryId=@subCategoryId,
        SubCategory=@subCategory,
        SubCategoryName=@subCategoryName,
        VisualItemReference=@visualRef,
        VisualParam=@visualParam,
        UnEquipable=@unEquipable,
        IsNew=@isNew,
        IsHot=@isHot,
        SellByCarType=@sellByCarType,
        MagicNumber=@magicNumber,
        ImportedFrom=@importedFrom,
        ImportedUtc=SYSUTCDATETIME(),
        UpdatedUtc=SYSUTCDATETIME()
    WHERE ShopId=@shopId;
END
ELSE
BEGIN
    INSERT INTO dbo.visual_item_catalog
    (ShopId,ItemCode,Category,CategoryIndex,Support,UseMito,UseHancoin,UseMileage,
     SourceMitoPrice,SourceMito7dPrice,SourceMito30dPrice,SourceMito90dPrice,SourceMito365dPrice,SourceMito0dPrice,
     SourceHancoin7dPrice,SourceHancoin30dPrice,SourceHancoin90dPrice,SourceHancoin365dPrice,SourceHancoin0dPrice,
     SourceMileage7dPrice,SourceMileage30dPrice,SourceMileage90dPrice,SourceMileage365dPrice,SourceMileage0dPrice,
     SourcePeriod7d,SourcePeriod30d,SourcePeriod90d,SourcePeriod365d,SourcePeriod0d,
     SourceBonusMito7d,SourceBonusMito30d,SourceBonusMito90d,SourceBonusMito365d,SourceBonusMito0d,
     SourceBonusSpeed,SourceBonusAccel,SourceBonusBoost,SourceBonusCrash,SourceBonusAssist,
     ClientRowIndex,DisplayName,Description,TopCategory,MainCategoryId,MainCategory,SubCategoryId,SubCategory,
     SubCategoryName,VisualItemReference,VisualParam,UnEquipable,IsNew,IsHot,SellByCarType,MagicNumber,ImportedFrom,ImportedUtc)
    VALUES
    (@shopId,@itemCode,@category,@categoryIndex,@support,@useMito,@useHancoin,@useMileage,
     @mito,@mito7,@mito30,@mito90,@mito365,@mito0,@hc7,@hc30,@hc90,@hc365,@hc0,
     @mile7,@mile30,@mile90,@mile365,@mile0,
     @period7,@period30,@period90,@period365,@period0,
     @bonusMito7,@bonusMito30,@bonusMito90,@bonusMito365,@bonusMito0,
     @bonusSpeed,@bonusAccel,@bonusBoost,@bonusCrash,@bonusAssist,
     @clientRowIndex,@displayName,@description,@topCategory,@mainCategoryId,@mainCategory,@subCategoryId,@subCategory,
     @subCategoryName,@visualRef,@visualParam,@unEquipable,@isNew,@isHot,@sellByCarType,@magicNumber,@importedFrom,SYSUTCDATETIME());
END;";

            using (var cmd = new Shared.Models.MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@shopId", shopId);
                cmd.Parameters.AddWithValue("@itemCode", DbText(
                    visual != null && !string.IsNullOrWhiteSpace(visual.ItemCode)
                        ? visual.ItemCode
                        : Get(row, "ItemName")));
                cmd.Parameters.AddWithValue("@category", DbText(Get(row, "Category")));
                cmd.Parameters.AddWithValue("@categoryIndex", visual == null ? 0 : visual.CategoryIndex);
                cmd.Parameters.AddWithValue("@support", IntValue(row, "Support"));
                cmd.Parameters.AddWithValue("@useMito", BoolValue(row, "Use Mito"));
                cmd.Parameters.AddWithValue("@useHancoin", BoolValue(row, "UseHancoin"));
                cmd.Parameters.AddWithValue("@useMileage", BoolValue(row, "Use Mileage"));

                cmd.Parameters.AddWithValue("@mito", DbInt(row, "Mito Price"));
                cmd.Parameters.AddWithValue("@mito7", DbInt(row, "Mito Price7D"));
                cmd.Parameters.AddWithValue("@mito30", DbInt(row, "Mito Price30D"));
                cmd.Parameters.AddWithValue("@mito90", DbInt(row, "Mito Price90D"));
                cmd.Parameters.AddWithValue("@mito365", DbInt(row, "Mito Price365D"));
                cmd.Parameters.AddWithValue("@mito0", DbInt(row, "Mito Price0D"));

                // The retail XLT labels Hancoin prices as "$Price".
                cmd.Parameters.AddWithValue("@hc7", DbInt(row, "$Price 7D"));
                cmd.Parameters.AddWithValue("@hc30", DbInt(row, "$Price 30D"));
                cmd.Parameters.AddWithValue("@hc90", DbInt(row, "$Price 90D"));
                cmd.Parameters.AddWithValue("@hc365", DbInt(row, "$Price 365D"));
                cmd.Parameters.AddWithValue("@hc0", DbInt(row, "$Price 0D"));

                cmd.Parameters.AddWithValue("@mile7", DbInt(row, "Mile Price7D"));
                cmd.Parameters.AddWithValue("@mile30", DbInt(row, "Mile Price30D"));
                cmd.Parameters.AddWithValue("@mile90", DbInt(row, "Mile Price90D"));
                cmd.Parameters.AddWithValue("@mile365", DbInt(row, "Mile Price365D"));
                cmd.Parameters.AddWithValue("@mile0", DbInt(row, "Mile Price0D"));

                cmd.Parameters.AddWithValue("@period7", DbInt(row, "Period 7D"));
                cmd.Parameters.AddWithValue("@period30", DbInt(row, "Period 30D"));
                cmd.Parameters.AddWithValue("@period90", DbInt(row, "Period 90D"));
                cmd.Parameters.AddWithValue("@period365", DbInt(row, "Period 365D"));
                cmd.Parameters.AddWithValue("@period0", DbInt(row, "Period 0D"));

                cmd.Parameters.AddWithValue("@bonusMito7", IntValue(row, "Bonus Mito 7D"));
                cmd.Parameters.AddWithValue("@bonusMito30", IntValue(row, "Bonus Mito 30D"));
                cmd.Parameters.AddWithValue("@bonusMito90", IntValue(row, "Bonus Mito 90D"));
                cmd.Parameters.AddWithValue("@bonusMito365", IntValue(row, "Bonus Mito 365D"));
                cmd.Parameters.AddWithValue("@bonusMito0", IntValue(row, "Bonus Mito 0D"));
                cmd.Parameters.AddWithValue("@bonusSpeed", IntValue(row, "Bonus Speed"));
                cmd.Parameters.AddWithValue("@bonusAccel", IntValue(row, "Bonus Accel"));
                cmd.Parameters.AddWithValue("@bonusBoost", IntValue(row, "Bonus Boost"));
                cmd.Parameters.AddWithValue("@bonusCrash", IntValue(row, "Bonus Crash"));
                cmd.Parameters.AddWithValue("@bonusAssist", IntValue(row, "Bonus Assist"));

                cmd.Parameters.AddWithValue("@clientRowIndex", DbInt(row, "Index"));
                cmd.Parameters.AddWithValue("@displayName", DbText(Get(row, "MarketName")));
                cmd.Parameters.AddWithValue("@description", DbText(Get(row, "Desc (Tooltip)")));
                cmd.Parameters.AddWithValue("@topCategory", DbText(Get(row, "Top Category")));
                cmd.Parameters.AddWithValue("@mainCategoryId", DbInt(row, "Main"));
                cmd.Parameters.AddWithValue("@mainCategory", DbText(Get(row, "Category")));
                cmd.Parameters.AddWithValue("@subCategoryId", DbInt(row, "Sub"));
                cmd.Parameters.AddWithValue("@subCategory", DbText(Get(row, "Sub Catergory")));
                cmd.Parameters.AddWithValue("@subCategoryName", DbText(Get(row, "Name")));
                cmd.Parameters.AddWithValue("@visualRef", DbInt(row, "Refer VisualItem"));
                cmd.Parameters.AddWithValue("@visualParam", DbText(visual == null ? string.Empty : visual.Param));
                cmd.Parameters.AddWithValue("@unEquipable", BoolValue(row, "UnEquipable"));
                cmd.Parameters.AddWithValue("@isNew", BoolValue(row, "New"));
                cmd.Parameters.AddWithValue("@isHot", BoolValue(row, "Hot"));
                cmd.Parameters.AddWithValue("@sellByCarType", DbInt(row, "SellByCarType"));
                cmd.Parameters.AddWithValue("@magicNumber", DbLong(row, "MagicNumber"));
                cmd.Parameters.AddWithValue("@importedFrom", DbText(sourcePath.Replace('\\', '/')));
                cmd.ExecuteNonQuery();
            }
        }

        private static string Get(Dictionary<string, string> row, string key)
        {
            string value;
            return row.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static bool TryInt(string value, out int result)
        {
            return int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out result);
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

        private static object DbInt(Dictionary<string, string> row, string key)
        {
            int value;
            return TryInt(Get(row, key), out value) ? (object)value : DBNull.Value;
        }

        private static object DbLong(Dictionary<string, string> row, string key)
        {
            long value;
            return long.TryParse(
                Get(row, key),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value)
                ? (object)value
                : DBNull.Value;
        }

        private static object DbText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value.Trim();
        }
    }
}
