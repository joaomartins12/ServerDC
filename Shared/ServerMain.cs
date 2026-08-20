using System;
using System.Collections.Generic;
using System.IO;
using Shared.Database;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;
using Shared.Util.Configuration;

namespace Shared
{
    /// <summary>
    ///     General methods needed by all servers.
    /// </summary>
    public abstract class ServerMain
    {
        public const int ProtocolVersion = 10249;

        public static List<VShopItemList.VShopItem> VisualItems;
        public static List<VehicleList.VehicleData> Vehicles;
        public static List<QuestTable.Quest> Quests;
        public static List<BasicItem> Items;
        public static Dictionary<int, KeyValuePair<ushort, long>> LevelTable;

        /// <summary>
        ///     Tries to find root folder and changes the working directory to it.
        ///     Exits if not successful.
        /// </summary>
        protected static void NavigateToRoot()
        {
            // Go back max 2 folders, the bins should be in /bin/(Debug|Release)
            for (var i = 0; i < 3; ++i)
            {
                if (Directory.Exists("system"))
                {
                    Log.InitializeStructuredLogging();
                    return;
                }

                Directory.SetCurrentDirectory("..");
            }

            Log.Error("Unable to find root directory.");
            ConsoleUtil.Exit(1);
        }

        /// <summary>
        ///     Tries to call conf's load method, exits on error.
        /// </summary>
        protected static void LoadConf(BaseConf conf)
        {
            Log.Info("Reading configuration...");

            try
            {
                conf.Load();
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "Unable to read configuration. ({0})", ex.Message);
                ConsoleUtil.Exit(1);
            }
        }

        /// <summary>
        ///     Tries to initialize database with the information from conf,
        ///     exits on error.
        /// </summary>
        protected static void InitDatabase(BaseDatabase db, BaseConf conf)
        {
            Log.Info("Initializing database...");

            try
            {
                db.Init(conf.Database.Host, conf.Database.Port, conf.Database.User, conf.Database.Pass,
                    conf.Database.Db);
                NormalizeDboSchema(db);
                EnsureVehicleCatalogKeyItemId(db);
            }
            catch (Exception ex)
            {
                Log.Error("Unable to open database connection. ({0})", ex.Message);
                ConsoleUtil.Exit(1);
            }
        }

        /// <summary>
        /// Adds the manually maintained vehicle key item id mapping column.
        /// Existing values are never overwritten by server startup migrations.
        /// </summary>
        private static void EnsureVehicleCatalogKeyItemId(BaseDatabase db)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.vehicle_catalog', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.vehicle_catalog', N'KeyItemId') IS NULL
BEGIN
    ALTER TABLE dbo.vehicle_catalog ADD KeyItemId VARCHAR(32) NULL;
END;";

            using (var connection = db.Connection)
            using (var command = new Shared.Models.MySqlCommand(sql, connection))
                command.ExecuteNonQuery();

            Log.Debug("Vehicle catalog KeyItemId migration complete.");
        }

        /// <summary>
        /// SQL Server always stores tables inside a schema. dbo is the canonical schema
        /// for this server. This migration repairs legacy/custom-schema tables by moving
        /// them to dbo when no dbo table with the same name already exists.
        /// </summary>
        private static void NormalizeDboSchema(BaseDatabase db)
        {
            const string sql = @"
IF SCHEMA_ID(N'dbo') IS NULL
    EXEC(N'CREATE SCHEMA dbo AUTHORIZATION db_owner');

DECLARE @TableName SYSNAME;
DECLARE @SchemaName SYSNAME;
DECLARE @Sql NVARCHAR(MAX);

DECLARE schema_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT t.name, s.name
FROM sys.tables AS t
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE s.name <> N'dbo'
  AND t.name IN
  (
      N'users', N'characters', N'vehicles', N'items', N'friends', N'quests',
      N'servers', N'shop', N'teams', N'updates',
      N'item_catalog', N'vehicle_catalog', N'vehicle_upgrade_catalog'
  )
  AND OBJECT_ID(N'dbo.' + QUOTENAME(t.name), N'U') IS NULL;

OPEN schema_cursor;
FETCH NEXT FROM schema_cursor INTO @TableName, @SchemaName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @Sql = N'ALTER SCHEMA dbo TRANSFER '
             + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName) + N';';
    EXEC sys.sp_executesql @Sql;
    FETCH NEXT FROM schema_cursor INTO @TableName, @SchemaName;
END

CLOSE schema_cursor;
DEALLOCATE schema_cursor;";

            using (var connection = db.Connection)
            using (var command = new Shared.Models.MySqlCommand(sql, connection))
                command.ExecuteNonQuery();

            Log.Debug("Database schema normalization complete (dbo).");
        }
    }
}
