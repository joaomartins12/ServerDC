using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Web.Script.Serialization;

namespace ServerManager
{
    internal static class VehicleCatalogImporter
    {
        internal sealed class ImportResult
        {
            public int Vehicles;
            public int Upgrades;
            public string JsonPath;
        }

        private sealed class CatalogRoot
        {
            public string generatedAtUtc { get; set; }
            public int count { get; set; }
            public List<CatalogVehicle> vehicles { get; set; }
        }

        private sealed class CatalogVehicle
        {
            public int? runtimeIndex { get; set; }
            public int? vehicleId { get; set; }
            public string name { get; set; }
            public string type { get; set; }
            public string typeString { get; set; }
            public bool? sellable { get; set; }
            public string grade { get; set; }
            public int? accel { get; set; }
            public int? speed { get; set; }
            public int? crash { get; set; }
            public int? boost { get; set; }
            public int? requiredLevel { get; set; }
            public int? level { get; set; }
            public List<CatalogUpgrade> upgrades { get; set; }
        }

        private sealed class CatalogUpgrade
        {
            public int? gradeIndex { get; set; }
            public string gradeName { get; set; }
            public string coupon { get; set; }
            public int? accel { get; set; }
            public int? speed { get; set; }
            public int? crash { get; set; }
            public int? boost { get; set; }
            public int? price { get; set; }
            public int? sell { get; set; }
            public int? closeSell { get; set; }
            public int? upgradeMito { get; set; }
            public decimal? efficiency { get; set; }
            public decimal? capacity { get; set; }
            public int? requiredLevel { get; set; }
        }

        public static string CatalogPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Catalogs", "VehicleCatalog.json");
        public static bool CatalogExists() => File.Exists(CatalogPath);

        public static ImportResult Import()
        {
            if (!CatalogExists())
                throw new FileNotFoundException("VehicleCatalog.json was not found. Start Game Server once to generate it, then stop Game Server before importing.", CatalogPath);

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
            var catalog = serializer.Deserialize<CatalogRoot>(File.ReadAllText(CatalogPath));
            if (catalog == null || catalog.vehicles == null || catalog.vehicles.Count == 0)
                throw new InvalidDataException("VehicleCatalog.json is empty or invalid.");

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

            var upgradeCount = 0;
            var vehicleCount = 0;
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                EnsureTables(connection);
                using (var tx = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var vehicle in catalog.vehicles)
                        {
                            if (!vehicle.vehicleId.HasValue)
                                continue;

                            UpsertVehicle(connection, tx, vehicle);
                            vehicleCount++;

                            if (vehicle.upgrades == null) continue;
                            foreach (var upgrade in vehicle.upgrades)
                            {
                                if (upgrade == null || !upgrade.gradeIndex.HasValue)
                                    continue;

                                UpsertUpgrade(connection, tx, vehicle.vehicleId.Value, upgrade);
                                upgradeCount++;
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

            return new ImportResult { Vehicles = vehicleCount, Upgrades = upgradeCount, JsonPath = CatalogPath };
        }

        private static void EnsureTables(SqlConnection connection)
        {
            const string sql = @"
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
        KeyItemId VARCHAR(32) NULL,
        IsEnabled BIT NOT NULL CONSTRAINT DF_vehicle_catalog_IsEnabled DEFAULT(1),
        ServerBuyPrice INT NULL,
        ServerSellPrice INT NULL,
        SourceUpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_vehicle_catalog_SourceUpdatedAt DEFAULT(SYSUTCDATETIME()),
        AdminUpdatedAt DATETIME2 NULL
    );
    CREATE UNIQUE INDEX UX_vehicle_catalog_RuntimeIndex ON dbo.vehicle_catalog(RuntimeIndex) WHERE RuntimeIndex IS NOT NULL;
    CREATE INDEX IX_vehicle_catalog_Name ON dbo.vehicle_catalog(Name);
    CREATE INDEX IX_vehicle_catalog_TypeString ON dbo.vehicle_catalog(TypeString);
END
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.vehicle_catalog', N'KeyItemId') IS NULL
        ALTER TABLE dbo.vehicle_catalog ADD KeyItemId VARCHAR(32) NULL;
END;

IF OBJECT_ID(N'dbo.vehicle_upgrade_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.vehicle_upgrade_catalog
    (
        VehicleId INT NOT NULL,
        GradeIndex INT NOT NULL,
        GradeName VARCHAR(16) NOT NULL,
        Coupon VARCHAR(128) NULL,
        Accel INT NULL,
        Speed INT NULL,
        Crash INT NULL,
        Boost INT NULL,
        SourcePrice INT NULL,
        SourceSell INT NULL,
        CloseSell INT NULL,
        UpgradeMito INT NULL,
        Efficiency DECIMAL(10,3) NULL,
        Capacity DECIMAL(10,3) NULL,
        RequiredLevel INT NULL,
        ServerPrice INT NULL,
        ServerSell INT NULL,
        SourceUpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_vehicle_upgrade_SourceUpdatedAt DEFAULT(SYSUTCDATETIME()),
        AdminUpdatedAt DATETIME2 NULL,
        CONSTRAINT PK_vehicle_upgrade_catalog PRIMARY KEY(VehicleId, GradeIndex),
        CONSTRAINT FK_vehicle_upgrade_catalog_vehicle FOREIGN KEY(VehicleId) REFERENCES dbo.vehicle_catalog(VehicleId)
    );
END
ELSE
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.vehicle_upgrade_catalog')
          AND name = N'Efficiency'
          AND system_type_id = TYPE_ID(N'int')
    )
        ALTER TABLE dbo.vehicle_upgrade_catalog ALTER COLUMN Efficiency DECIMAL(10,3) NULL;

    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.vehicle_upgrade_catalog')
          AND name = N'Capacity'
          AND system_type_id = TYPE_ID(N'int')
    )
        ALTER TABLE dbo.vehicle_upgrade_catalog ALTER COLUMN Capacity DECIMAL(10,3) NULL;
END;";
            using (var cmd = new SqlCommand(sql, connection)) cmd.ExecuteNonQuery();
        }

        private static void UpsertVehicle(SqlConnection connection, SqlTransaction tx, CatalogVehicle v)
        {
            const string sql = @"
MERGE dbo.vehicle_catalog AS target
USING (SELECT @VehicleId AS VehicleId) AS source ON target.VehicleId=source.VehicleId
WHEN MATCHED THEN UPDATE SET RuntimeIndex=@RuntimeIndex,Name=@Name,Type=@Type,TypeString=@TypeString,
Sellable=@Sellable,Grade=@Grade,BaseAccel=@BaseAccel,BaseSpeed=@BaseSpeed,BaseCrash=@BaseCrash,
BaseBoost=@BaseBoost,RequiredLevel=@RequiredLevel,Level=@Level,SourceUpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(VehicleId,RuntimeIndex,Name,Type,TypeString,Sellable,Grade,BaseAccel,BaseSpeed,
BaseCrash,BaseBoost,RequiredLevel,Level,IsEnabled,SourceUpdatedAt)
VALUES(@VehicleId,@RuntimeIndex,@Name,@Type,@TypeString,@Sellable,@Grade,@BaseAccel,@BaseSpeed,@BaseCrash,
@BaseBoost,@RequiredLevel,@Level,1,SYSUTCDATETIME());";
            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@VehicleId", v.vehicleId.Value);
                cmd.Parameters.AddWithValue("@RuntimeIndex", Db(v.runtimeIndex));
                cmd.Parameters.AddWithValue("@Name", Db(v.name));
                cmd.Parameters.AddWithValue("@Type", Db(v.type));
                cmd.Parameters.AddWithValue("@TypeString", Db(v.typeString));
                cmd.Parameters.AddWithValue("@Sellable", v.sellable ?? false);
                cmd.Parameters.AddWithValue("@Grade", Db(v.grade));
                cmd.Parameters.AddWithValue("@BaseAccel", Db(v.accel));
                cmd.Parameters.AddWithValue("@BaseSpeed", Db(v.speed));
                cmd.Parameters.AddWithValue("@BaseCrash", Db(v.crash));
                cmd.Parameters.AddWithValue("@BaseBoost", Db(v.boost));
                cmd.Parameters.AddWithValue("@RequiredLevel", Db(v.requiredLevel));
                cmd.Parameters.AddWithValue("@Level", Db(v.level));
                cmd.ExecuteNonQuery();
            }
        }

        private static void UpsertUpgrade(SqlConnection connection, SqlTransaction tx, int vehicleId, CatalogUpgrade u)
        {
            const string sql = @"
MERGE dbo.vehicle_upgrade_catalog AS target
USING (SELECT @VehicleId AS VehicleId,@GradeIndex AS GradeIndex) AS source
ON target.VehicleId=source.VehicleId AND target.GradeIndex=source.GradeIndex
WHEN MATCHED THEN UPDATE SET GradeName=@GradeName,Coupon=@Coupon,Accel=@Accel,Speed=@Speed,Crash=@Crash,
Boost=@Boost,SourcePrice=@SourcePrice,SourceSell=@SourceSell,CloseSell=@CloseSell,UpgradeMito=@UpgradeMito,
Efficiency=@Efficiency,Capacity=@Capacity,RequiredLevel=@RequiredLevel,SourceUpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(VehicleId,GradeIndex,GradeName,Coupon,Accel,Speed,Crash,Boost,SourcePrice,
SourceSell,CloseSell,UpgradeMito,Efficiency,Capacity,RequiredLevel,SourceUpdatedAt)
VALUES(@VehicleId,@GradeIndex,@GradeName,@Coupon,@Accel,@Speed,@Crash,@Boost,@SourcePrice,@SourceSell,
@CloseSell,@UpgradeMito,@Efficiency,@Capacity,@RequiredLevel,SYSUTCDATETIME());";
            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@VehicleId", vehicleId);
                cmd.Parameters.AddWithValue("@GradeIndex", u.gradeIndex.Value);
                cmd.Parameters.AddWithValue("@GradeName", Db(u.gradeName ?? ("V" + (u.gradeIndex.Value + 1))));
                cmd.Parameters.AddWithValue("@Coupon", Db(u.coupon));
                cmd.Parameters.AddWithValue("@Accel", Db(u.accel));
                cmd.Parameters.AddWithValue("@Speed", Db(u.speed));
                cmd.Parameters.AddWithValue("@Crash", Db(u.crash));
                cmd.Parameters.AddWithValue("@Boost", Db(u.boost));
                cmd.Parameters.AddWithValue("@SourcePrice", Db(u.price));
                cmd.Parameters.AddWithValue("@SourceSell", Db(u.sell));
                cmd.Parameters.AddWithValue("@CloseSell", Db(u.closeSell));
                cmd.Parameters.AddWithValue("@UpgradeMito", Db(u.upgradeMito));
                AddDecimal(cmd, "@Efficiency", u.efficiency);
                AddDecimal(cmd, "@Capacity", u.capacity);
                cmd.Parameters.AddWithValue("@RequiredLevel", Db(u.requiredLevel));
                cmd.ExecuteNonQuery();
            }
        }

        private static void AddDecimal(SqlCommand cmd, string name, decimal? value)
        {
            var parameter = cmd.Parameters.Add(name, SqlDbType.Decimal);
            parameter.Precision = 10;
            parameter.Scale = 3;
            parameter.Value = value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static object Db(object value) => value ?? DBNull.Value;
    }
}
