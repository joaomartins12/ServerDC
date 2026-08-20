using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
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
            public int runtimeIndex { get; set; }
            public int vehicleId { get; set; }
            public string name { get; set; }
            public string type { get; set; }
            public string typeString { get; set; }
            public bool sellable { get; set; }
            public string grade { get; set; }
            public int accel { get; set; }
            public int speed { get; set; }
            public int crash { get; set; }
            public int boost { get; set; }
            public int requiredLevel { get; set; }
            public int level { get; set; }
            public List<CatalogUpgrade> upgrades { get; set; }
        }

        private sealed class CatalogUpgrade
        {
            public int gradeIndex { get; set; }
            public string gradeName { get; set; }
            public string coupon { get; set; }
            public int accel { get; set; }
            public int speed { get; set; }
            public int crash { get; set; }
            public int boost { get; set; }
            public int price { get; set; }
            public int sell { get; set; }
            public int closeSell { get; set; }
            public int upgradeMito { get; set; }
            public int efficiency { get; set; }
            public int capacity { get; set; }
            public int requiredLevel { get; set; }
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
                            UpsertVehicle(connection, tx, vehicle);
                            if (vehicle.upgrades == null) continue;
                            foreach (var upgrade in vehicle.upgrades)
                            {
                                UpsertUpgrade(connection, tx, vehicle.vehicleId, upgrade);
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

            return new ImportResult { Vehicles = catalog.vehicles.Count, Upgrades = upgradeCount, JsonPath = CatalogPath };
        }

        private static void EnsureTables(SqlConnection connection)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.vehicle_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.vehicle_catalog
    (
        VehicleId INT NOT NULL CONSTRAINT PK_vehicle_catalog PRIMARY KEY,
        RuntimeIndex INT NOT NULL,
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
        AdminUpdatedAt DATETIME2 NULL
    );
    CREATE UNIQUE INDEX UX_vehicle_catalog_RuntimeIndex ON dbo.vehicle_catalog(RuntimeIndex);
    CREATE INDEX IX_vehicle_catalog_Name ON dbo.vehicle_catalog(Name);
    CREATE INDEX IX_vehicle_catalog_TypeString ON dbo.vehicle_catalog(TypeString);
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
        Efficiency INT NULL,
        Capacity INT NULL,
        RequiredLevel INT NULL,
        ServerPrice INT NULL,
        ServerSell INT NULL,
        SourceUpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_vehicle_upgrade_SourceUpdatedAt DEFAULT(SYSUTCDATETIME()),
        AdminUpdatedAt DATETIME2 NULL,
        CONSTRAINT PK_vehicle_upgrade_catalog PRIMARY KEY(VehicleId, GradeIndex),
        CONSTRAINT FK_vehicle_upgrade_catalog_vehicle FOREIGN KEY(VehicleId) REFERENCES dbo.vehicle_catalog(VehicleId)
    );
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
                cmd.Parameters.AddWithValue("@VehicleId", v.vehicleId);
                cmd.Parameters.AddWithValue("@RuntimeIndex", v.runtimeIndex);
                cmd.Parameters.AddWithValue("@Name", Db(v.name));
                cmd.Parameters.AddWithValue("@Type", Db(v.type));
                cmd.Parameters.AddWithValue("@TypeString", Db(v.typeString));
                cmd.Parameters.AddWithValue("@Sellable", v.sellable);
                cmd.Parameters.AddWithValue("@Grade", Db(v.grade));
                cmd.Parameters.AddWithValue("@BaseAccel", v.accel);
                cmd.Parameters.AddWithValue("@BaseSpeed", v.speed);
                cmd.Parameters.AddWithValue("@BaseCrash", v.crash);
                cmd.Parameters.AddWithValue("@BaseBoost", v.boost);
                cmd.Parameters.AddWithValue("@RequiredLevel", v.requiredLevel);
                cmd.Parameters.AddWithValue("@Level", v.level);
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
                cmd.Parameters.AddWithValue("@GradeIndex", u.gradeIndex);
                cmd.Parameters.AddWithValue("@GradeName", Db(u.gradeName));
                cmd.Parameters.AddWithValue("@Coupon", Db(u.coupon));
                cmd.Parameters.AddWithValue("@Accel", u.accel);
                cmd.Parameters.AddWithValue("@Speed", u.speed);
                cmd.Parameters.AddWithValue("@Crash", u.crash);
                cmd.Parameters.AddWithValue("@Boost", u.boost);
                cmd.Parameters.AddWithValue("@SourcePrice", u.price);
                cmd.Parameters.AddWithValue("@SourceSell", u.sell);
                cmd.Parameters.AddWithValue("@CloseSell", u.closeSell);
                cmd.Parameters.AddWithValue("@UpgradeMito", u.upgradeMito);
                cmd.Parameters.AddWithValue("@Efficiency", u.efficiency);
                cmd.Parameters.AddWithValue("@Capacity", u.capacity);
                cmd.Parameters.AddWithValue("@RequiredLevel", u.requiredLevel);
                cmd.ExecuteNonQuery();
            }
        }

        private static object Db(object value) => value ?? DBNull.Value;
    }
}
