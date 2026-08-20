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

        protected static void NavigateToRoot()
        {
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

        protected static void InitDatabase(BaseDatabase db, BaseConf conf)
        {
            Log.Info("Initializing database...");

            try
            {
                db.Init(conf.Database.Host, conf.Database.Port, conf.Database.User, conf.Database.Pass,
                    conf.Database.Db);
                NormalizeDboSchema(db);
                EnsureVehicleCatalogKeyItemId(db);
                EnsureCharacterProgressSchema(db);
            }
            catch (Exception ex)
            {
                Log.Error("Unable to open database connection. ({0})", ex.Message);
                ConsoleUtil.Exit(1);
            }
        }

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
        /// Persistent driver statistics and license/title state. All server executables
        /// start nearly together, so SQL Server application locking serializes this
        /// additive migration across processes.
        /// </summary>
        private static void EnsureCharacterProgressSchema(BaseDatabase db)
        {
            const string sql = @"
DECLARE @LockResult INT;
EXEC @LockResult = sys.sp_getapplock
    @Resource=N'DCServer.CharacterProgressMigration',
    @LockMode=N'Exclusive',
    @LockOwner=N'Session',
    @LockTimeout=60000;
IF @LockResult < 0
    THROW 51000, 'Unable to acquire character progress migration lock.', 1;

IF OBJECT_ID(N'dbo.characters', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.characters', N'CurrentLicenseId') IS NULL
        ALTER TABLE dbo.characters ADD CurrentLicenseId INT NOT NULL CONSTRAINT DF_characters_CurrentLicenseId DEFAULT (7000);
    IF COL_LENGTH(N'dbo.characters', N'PvpCount') IS NULL
        ALTER TABLE dbo.characters ADD PvpCount BIGINT NOT NULL CONSTRAINT DF_characters_PvpCount DEFAULT (0);
    IF COL_LENGTH(N'dbo.characters', N'PvpWinCount') IS NULL
        ALTER TABLE dbo.characters ADD PvpWinCount BIGINT NOT NULL CONSTRAINT DF_characters_PvpWinCount DEFAULT (0);
    IF COL_LENGTH(N'dbo.characters', N'PvpPoint') IS NULL
        ALTER TABLE dbo.characters ADD PvpPoint BIGINT NOT NULL CONSTRAINT DF_characters_PvpPoint DEFAULT (0);
    IF COL_LENGTH(N'dbo.characters', N'TeamPvpCount') IS NULL
        ALTER TABLE dbo.characters ADD TeamPvpCount BIGINT NOT NULL CONSTRAINT DF_characters_TeamPvpCount DEFAULT (0);
    IF COL_LENGTH(N'dbo.characters', N'TeamPvpWinCount') IS NULL
        ALTER TABLE dbo.characters ADD TeamPvpWinCount BIGINT NOT NULL CONSTRAINT DF_characters_TeamPvpWinCount DEFAULT (0);
    IF COL_LENGTH(N'dbo.characters', N'TeamPvpPoint') IS NULL
        ALTER TABLE dbo.characters ADD TeamPvpPoint BIGINT NOT NULL CONSTRAINT DF_characters_TeamPvpPoint DEFAULT (0);
    IF COL_LENGTH(N'dbo.characters', N'QuickCount') IS NULL
        ALTER TABLE dbo.characters ADD QuickCount BIGINT NOT NULL CONSTRAINT DF_characters_QuickCount DEFAULT (0);
END;

IF OBJECT_ID(N'dbo.character_licenses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.character_licenses
    (
        CID BIGINT NOT NULL,
        LicenseId INT NOT NULL,
        UnlockedDate BIGINT NOT NULL CONSTRAINT DF_character_licenses_UnlockedDate DEFAULT (0),
        IsNew BIT NOT NULL CONSTRAINT DF_character_licenses_IsNew DEFAULT (1),
        CONSTRAINT PK_character_licenses PRIMARY KEY (CID, LicenseId),
        CONSTRAINT FK_character_licenses_characters FOREIGN KEY (CID) REFERENCES dbo.characters(CID) ON DELETE CASCADE
    );
END;

IF OBJECT_ID(N'dbo.character_progress', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.character_progress
    (
        CID BIGINT NOT NULL,
        ProgressKey VARCHAR(64) NOT NULL,
        ProgressValue BIGINT NOT NULL CONSTRAINT DF_character_progress_Value DEFAULT (0),
        UpdatedAt BIGINT NOT NULL CONSTRAINT DF_character_progress_UpdatedAt DEFAULT (0),
        CONSTRAINT PK_character_progress PRIMARY KEY (CID, ProgressKey),
        CONSTRAINT FK_character_progress_characters FOREIGN KEY (CID) REFERENCES dbo.characters(CID) ON DELETE CASCADE
    );
END;

MERGE dbo.character_licenses AS target
USING (SELECT CID, CAST(7000 AS INT) AS LicenseId FROM dbo.characters) AS source
ON target.CID = source.CID AND target.LicenseId = source.LicenseId
WHEN NOT MATCHED THEN
    INSERT (CID, LicenseId, UnlockedDate, IsNew) VALUES (source.CID, source.LicenseId, 0, 0);

UPDATE dbo.characters SET CurrentLicenseId = 7000 WHERE CurrentLicenseId IS NULL OR CurrentLicenseId <= 0;

EXEC sys.sp_releaseapplock
    @Resource=N'DCServer.CharacterProgressMigration',
    @LockOwner=N'Session';";

            using (var connection = db.Connection)
            using (var command = new Shared.Models.MySqlCommand(sql, connection))
                command.ExecuteNonQuery();

            Log.Debug("Character stats/license migration complete.");
        }

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
      N'item_catalog', N'vehicle_catalog', N'vehicle_upgrade_catalog',
      N'character_licenses', N'character_progress'
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

namespace Shared.Models
{
    /// <summary>
    /// Persistence facade for profile statistics and the license requirement system.
    /// ProgressKey maps directly to License_GC.xlt identifiers (GC_BattleTeam, etc.).
    /// </summary>
    public static class CharacterProgressModel
    {
        public const int DefaultLicenseId = 7000;

        public static int LoadPersistentStats(MySqlConnection connection, Character character)
        {
            if (connection == null || character == null) return DefaultLicenseId;

            using (var command = new MySqlCommand(
                @"SELECT CurrentLicenseId, PvpCount, PvpWinCount, PvpPoint,
                         TeamPvpCount, TeamPvpWinCount, TeamPvpPoint, QuickCount, Mileage
                  FROM dbo.characters WHERE CID=@cid", connection))
            {
                command.Parameters.AddWithValue("@cid", character.Id);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return DefaultLicenseId;

                    character.PvpCount = Convert.ToUInt32(reader["PvpCount"]);
                    character.PvpWinCount = Convert.ToUInt32(reader["PvpWinCount"]);
                    character.PvpPoint = Convert.ToUInt32(reader["PvpPoint"]);
                    character.TeamPvpCount = Convert.ToUInt32(reader["TeamPvpCount"]);
                    character.TeamPvpWinCount = Convert.ToUInt32(reader["TeamPvpWinCount"]);
                    character.TeamPvpPoint = Convert.ToUInt32(reader["TeamPvpPoint"]);
                    character.QuickCount = Convert.ToUInt32(reader["QuickCount"]);
                    character.TotalDistance = Convert.ToSingle(reader["Mileage"]);
                    return Convert.ToInt32(reader["CurrentLicenseId"]);
                }
            }
        }

        public static void UpdateMileage(MySqlConnection connection, Character character)
        {
            if (connection == null || character == null) return;
            using (var command = new MySqlCommand(
                "UPDATE dbo.characters SET Mileage=@mileage WHERE CID=@cid", connection))
            {
                command.Parameters.AddWithValue("@mileage", character.TotalDistance);
                command.Parameters.AddWithValue("@cid", character.Id);
                command.ExecuteNonQuery();
            }
        }

        public static void RecordBattleResult(MySqlConnection connection, Character character, bool teamBattle, bool won, uint points, long unixTime)
        {
            if (connection == null || character == null) return;

            var totalColumn = teamBattle ? "TeamPvpCount" : "PvpCount";
            var winsColumn = teamBattle ? "TeamPvpWinCount" : "PvpWinCount";
            var pointColumn = teamBattle ? "TeamPvpPoint" : "PvpPoint";
            var progressKey = teamBattle ? "GC_BattleTeam" : "GC_BattlePersonal";

            var sql = "UPDATE dbo.characters SET " + totalColumn + "=" + totalColumn + "+1, " +
                      winsColumn + "=" + winsColumn + "+@win, " +
                      pointColumn + "=" + pointColumn + "+@points WHERE CID=@cid";
            using (var command = new MySqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@win", won ? 1 : 0);
                command.Parameters.AddWithValue("@points", points);
                command.Parameters.AddWithValue("@cid", character.Id);
                command.ExecuteNonQuery();
            }

            if (teamBattle)
            {
                character.TeamPvpCount++;
                if (won) character.TeamPvpWinCount++;
                character.TeamPvpPoint += points;
            }
            else
            {
                character.PvpCount++;
                if (won) character.PvpWinCount++;
                character.PvpPoint += points;
            }

            IncrementProgress(connection, character.Id, progressKey, 1, unixTime);
        }

        public static int GetCurrentLicense(MySqlConnection connection, ulong cid)
        {
            using (var command = new MySqlCommand(
                "SELECT CurrentLicenseId FROM dbo.characters WHERE CID=@cid", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? DefaultLicenseId : Convert.ToInt32(value);
            }
        }

        public static bool SetCurrentLicense(MySqlConnection connection, ulong cid, int licenseId)
        {
            if (!HasLicense(connection, cid, licenseId)) return false;
            using (var command = new MySqlCommand(
                "UPDATE dbo.characters SET CurrentLicenseId=@license WHERE CID=@cid", connection))
            {
                command.Parameters.AddWithValue("@license", licenseId);
                command.Parameters.AddWithValue("@cid", cid);
                return command.ExecuteNonQuery() == 1;
            }
        }

        public static bool HasLicense(MySqlConnection connection, ulong cid, int licenseId)
        {
            using (var command = new MySqlCommand(
                "SELECT COUNT(1) FROM dbo.character_licenses WHERE CID=@cid AND LicenseId=@license", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                command.Parameters.AddWithValue("@license", licenseId);
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
        }

        public static void UnlockLicense(MySqlConnection connection, ulong cid, int licenseId, long unixTime)
        {
            using (var command = new MySqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.character_licenses WHERE CID=@cid AND LicenseId=@license)
    INSERT INTO dbo.character_licenses(CID, LicenseId, UnlockedDate, IsNew)
    VALUES(@cid, @license, @time, 1);", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                command.Parameters.AddWithValue("@license", licenseId);
                command.Parameters.AddWithValue("@time", unixTime);
                command.ExecuteNonQuery();
            }
        }

        public static List<int> GetUnlockedLicenses(MySqlConnection connection, ulong cid)
        {
            var result = new List<int>();
            using (var command = new MySqlCommand(
                "SELECT LicenseId FROM dbo.character_licenses WHERE CID=@cid ORDER BY LicenseId", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(Convert.ToInt32(reader["LicenseId"]));
            }
            return result;
        }

        public static long GetProgress(MySqlConnection connection, ulong cid, string progressKey)
        {
            if (string.IsNullOrWhiteSpace(progressKey)) return 0L;
            using (var command = new MySqlCommand(
                "SELECT ProgressValue FROM dbo.character_progress WHERE CID=@cid AND ProgressKey=@key", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                command.Parameters.AddWithValue("@key", progressKey);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0L : Convert.ToInt64(value);
            }
        }

        public static long IncrementProgress(MySqlConnection connection, ulong cid, string progressKey, long amount, long unixTime)
        {
            if (string.IsNullOrWhiteSpace(progressKey) || amount == 0) return GetProgress(connection, cid, progressKey);
            using (var command = new MySqlCommand(@"
MERGE dbo.character_progress AS target
USING (SELECT @cid AS CID, @key AS ProgressKey) AS source
ON target.CID=source.CID AND target.ProgressKey=source.ProgressKey
WHEN MATCHED THEN UPDATE SET ProgressValue=target.ProgressValue + @amount, UpdatedAt=@time
WHEN NOT MATCHED THEN INSERT(CID, ProgressKey, ProgressValue, UpdatedAt)
VALUES(@cid, @key, @amount, @time);", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                command.Parameters.AddWithValue("@key", progressKey);
                command.Parameters.AddWithValue("@amount", amount);
                command.Parameters.AddWithValue("@time", unixTime);
                command.ExecuteNonQuery();
            }
            return GetProgress(connection, cid, progressKey);
        }
    }
}
