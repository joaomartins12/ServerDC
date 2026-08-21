using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Shared.Models;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    /// <summary>
    /// SQL Server backing store for the visual shop. Shop IDs, prices, ownership,
    /// equipped state and balances are resolved server-side and committed atomically.
    /// </summary>
    public static class VisualShopDatabase
    {
        public const string CatalogTable = "dbo.visual_item_catalog";
        public const string InventoryTable = "dbo.visual_items";

        public enum CurrencyType
        {
            Mito = 0,
            Hancoin = 1,
            Mileage = 2
        }

        public sealed class VisualInventoryRow
        {
            public long Id;
            public ulong CharacterId;
            public uint CarId;
            public uint ShopId;
            public uint InventoryIndex;
            public int CategoryIndex;
            public int Support;
            public int ItemState;
            public string Data;
            public int Period;
            public long CreateTime;
            public long UpdateTime;
            public long ExpireTime;
            public int BonusSpeed;
            public int BonusCrash;
            public int BonusAccel;
            public int BonusBoost;
        }

        public sealed class VisualStatBonus
        {
            public int Speed;
            public int Crash;
            public int Accel;
            public int Boost;
        }

        public sealed class PurchaseResult
        {
            public bool Success;
            public string Error;
            public uint ShopId;
            public uint CarId;
            public uint InventoryIndex;
            public int CategoryIndex;
            public int Support;
            public int Period;
            public CurrencyType Currency;
            public int Price;
            public int BonusMito;
            public bool Equipped;
        }

        private sealed class CatalogRow
        {
            public int ShopId;
            public int CategoryIndex;
            public int Support;
            public bool UseMito;
            public bool UseHancoin;
            public bool UseMileage;
            public int? MitoPrice;
            public int? Mito7;
            public int? Mito30;
            public int? Mito90;
            public int? Mito365;
            public int? Mito0;
            public int? Hc7;
            public int? Hc30;
            public int? Hc90;
            public int? Hc365;
            public int? Hc0;
            public int? Mileage7;
            public int? Mileage30;
            public int? Mileage90;
            public int? Mileage365;
            public int? Mileage0;
            public int BonusMito7;
            public int BonusMito30;
            public int BonusMito90;
            public int BonusMito365;
            public int BonusMito0;
        }

        public static void EnsureSchemaAndSynchronize(MySqlConnection conn, IList<VShopItemList.VShopItem> items)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            EnsureSchema(conn);
            SynchronizeCatalog(conn, items);
        }

        public static void EnsureSchema(MySqlConnection conn)
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

        SourceMitoPrice INT NULL,
        SourceMito7dPrice INT NULL,
        SourceMito30dPrice INT NULL,
        SourceMito90dPrice INT NULL,
        SourceMito365dPrice INT NULL,
        SourceMito0dPrice INT NULL,
        SourceHancoin7dPrice INT NULL,
        SourceHancoin30dPrice INT NULL,
        SourceHancoin90dPrice INT NULL,
        SourceHancoin365dPrice INT NULL,
        SourceHancoin0dPrice INT NULL,
        SourceMileage7dPrice INT NULL,
        SourceMileage30dPrice INT NULL,
        SourceMileage90dPrice INT NULL,
        SourceMileage365dPrice INT NULL,
        SourceMileage0dPrice INT NULL,

        ServerMitoPrice INT NULL,
        ServerMito7dPrice INT NULL,
        ServerMito30dPrice INT NULL,
        ServerMito90dPrice INT NULL,
        ServerMito365dPrice INT NULL,
        ServerMito0dPrice INT NULL,
        ServerHancoin7dPrice INT NULL,
        ServerHancoin30dPrice INT NULL,
        ServerHancoin90dPrice INT NULL,
        ServerHancoin365dPrice INT NULL,
        ServerHancoin0dPrice INT NULL,
        ServerMileage7dPrice INT NULL,
        ServerMileage30dPrice INT NULL,
        ServerMileage90dPrice INT NULL,
        ServerMileage365dPrice INT NULL,
        ServerMileage0dPrice INT NULL,

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
        ServerBonusSpeed INT NULL,
        ServerBonusCrash INT NULL,
        ServerBonusAccel INT NULL,
        ServerBonusBoost INT NULL,
        ServerBonusAssist INT NULL,
        UpdatedUtc DATETIME2 NOT NULL CONSTRAINT DF_visual_item_catalog_UpdatedUtc DEFAULT (SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.visual_items', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.visual_items
    (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_visual_items PRIMARY KEY,
        CharacterId BIGINT NOT NULL,
        CarId BIGINT NOT NULL,
        ShopId INT NOT NULL,
        InventoryIndex INT NOT NULL,
        CategoryIndex INT NOT NULL CONSTRAINT DF_visual_items_CategoryIndex DEFAULT (0),
        Support INT NOT NULL CONSTRAINT DF_visual_items_Support DEFAULT (0),
        ItemState INT NOT NULL CONSTRAINT DF_visual_items_ItemState DEFAULT (0),
        Data NVARCHAR(40) NULL,
        Period INT NOT NULL CONSTRAINT DF_visual_items_Period DEFAULT (0),
        CreateTime BIGINT NOT NULL CONSTRAINT DF_visual_items_CreateTime DEFAULT (0),
        UpdateTime BIGINT NOT NULL CONSTRAINT DF_visual_items_UpdateTime DEFAULT (0),
        ExpireTime BIGINT NOT NULL CONSTRAINT DF_visual_items_ExpireTime DEFAULT (0),
        CurrencyType INT NOT NULL CONSTRAINT DF_visual_items_CurrencyType DEFAULT (0),
        PaidMito BIGINT NOT NULL CONSTRAINT DF_visual_items_PaidMito DEFAULT (0),
        PaidHancoin BIGINT NOT NULL CONSTRAINT DF_visual_items_PaidHancoin DEFAULT (0),
        PaidMileage BIGINT NOT NULL CONSTRAINT DF_visual_items_PaidMileage DEFAULT (0)
    );
    CREATE UNIQUE INDEX UX_visual_items_Character_Inventory ON dbo.visual_items(CharacterId, InventoryIndex);
    CREATE INDEX IX_visual_items_Character_Car ON dbo.visual_items(CharacterId, CarId);
    CREATE INDEX IX_visual_items_Equipped ON dbo.visual_items(CharacterId, CarId, CategoryIndex, ItemState);
END;

-- Migration for databases created by the first visual-shop schema revision.
IF COL_LENGTH('dbo.visual_item_catalog','SourceMileage7dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceMileage7dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SourceMileage30dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceMileage30dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SourceMileage90dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceMileage90dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SourceMileage365dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceMileage365dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SourceMileage0dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceMileage0dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerMileage7dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerMileage7dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerMileage30dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerMileage30dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerMileage90dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerMileage90dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerMileage365dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerMileage365dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerMileage0dPrice') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerMileage0dPrice INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusMito7d') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusMito7d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito7_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusMito30d') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusMito30d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito30_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusMito90d') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusMito90d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito90_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusMito365d') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusMito365d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito365_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusMito0d') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusMito0d INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusMito0_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusSpeed') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusSpeed INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusSpeed_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusCrash') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusCrash INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusCrash_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusAccel') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusAccel INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusAccel_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusBoost') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusBoost INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusBoost_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','SourceBonusAssist') IS NULL ALTER TABLE dbo.visual_item_catalog ADD SourceBonusAssist INT NOT NULL CONSTRAINT DF_vcatalog_SourceBonusAssist_m DEFAULT (0);
IF COL_LENGTH('dbo.visual_item_catalog','ServerBonusSpeed') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerBonusSpeed INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerBonusCrash') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerBonusCrash INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerBonusAccel') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerBonusAccel INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerBonusBoost') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerBonusBoost INT NULL;
IF COL_LENGTH('dbo.visual_item_catalog','ServerBonusAssist') IS NULL ALTER TABLE dbo.visual_item_catalog ADD ServerBonusAssist INT NULL;";

            using (var cmd = new MySqlCommand(sql, conn))
                cmd.ExecuteNonQuery();
        }

        private static void SynchronizeCatalog(MySqlConnection conn, IList<VShopItemList.VShopItem> items)
        {
            if (items == null) return;

            var synchronized = 0;
            foreach (var item in items)
            {
                if (item == null) continue;
                int shopId;
                if (!int.TryParse(item.UniqueId, NumberStyles.Integer, CultureInfo.InvariantCulture, out shopId))
                    continue;

                const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.visual_item_catalog WHERE ShopId=@shopId)
BEGIN
    UPDATE dbo.visual_item_catalog SET
        ItemCode=@itemCode, Category=@category, CategoryIndex=@categoryIndex, Support=@support,
        UseMito=@useMito, UseHancoin=@useHancoin, UseMileage=@useMileage,
        SourceMitoPrice=@mito, SourceMito7dPrice=@mito7, SourceMito30dPrice=@mito30,
        SourceMito90dPrice=@mito90, SourceMito365dPrice=@mito365, SourceMito0dPrice=@mito0,
        SourceHancoin7dPrice=@hc7, SourceHancoin30dPrice=@hc30, SourceHancoin90dPrice=@hc90,
        SourceHancoin365dPrice=@hc365, SourceHancoin0dPrice=@hc0,
        SourceMileage7dPrice=@mile7, SourceMileage30dPrice=@mile30, SourceMileage90dPrice=@mile90,
        SourceMileage365dPrice=@mile365, SourceMileage0dPrice=@mile0,
        SourceBonusMito7d=@bonusMito7, SourceBonusMito30d=@bonusMito30, SourceBonusMito90d=@bonusMito90,
        SourceBonusMito365d=@bonusMito365, SourceBonusMito0d=@bonusMito0,
        SourceBonusSpeed=@bonusSpeed, SourceBonusCrash=@bonusCrash,
        SourceBonusAccel=@bonusAccel, SourceBonusBoost=@bonusBoost, SourceBonusAssist=@bonusAssist,
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
     SourceBonusMito7d,SourceBonusMito30d,SourceBonusMito90d,SourceBonusMito365d,SourceBonusMito0d,
     SourceBonusSpeed,SourceBonusCrash,SourceBonusAccel,SourceBonusBoost,SourceBonusAssist)
    VALUES
    (@shopId,@itemCode,@category,@categoryIndex,@support,@useMito,@useHancoin,@useMileage,
     @mito,@mito7,@mito30,@mito90,@mito365,@mito0,@hc7,@hc30,@hc90,@hc365,@hc0,
     @mile7,@mile30,@mile90,@mile365,@mile0,@bonusMito7,@bonusMito30,@bonusMito90,@bonusMito365,@bonusMito0,
     @bonusSpeed,@bonusCrash,@bonusAccel,@bonusBoost,@bonusAssist);
END;";

                using (var cmd = new MySqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@shopId", shopId);
                    cmd.Parameters.AddWithValue("@itemCode", DbString(item.ItemName));
                    cmd.Parameters.AddWithValue("@category", DbString(item.Category));
                    cmd.Parameters.AddWithValue("@categoryIndex", ParseInt(item.CategoryIndex));
                    cmd.Parameters.AddWithValue("@support", ParseInt(item.Support));
                    cmd.Parameters.AddWithValue("@useMito", ParseBool(item.UseMito));
                    cmd.Parameters.AddWithValue("@useHancoin", ParseBool(item.UseHancoin));
                    cmd.Parameters.AddWithValue("@useMileage", ParseBool(item.UseMileage));
                    cmd.Parameters.AddWithValue("@mito", DbInt(item.MitoPrice));
                    cmd.Parameters.AddWithValue("@mito7", DbInt(item.Mito7dPrice));
                    cmd.Parameters.AddWithValue("@mito30", DbInt(item.Mito30dPrice));
                    cmd.Parameters.AddWithValue("@mito90", DbInt(item.Mito90dPrice));
                    cmd.Parameters.AddWithValue("@mito365", DbInt(item.Mito365dPrice));
                    cmd.Parameters.AddWithValue("@mito0", DbInt(item.Mito0dPrice));
                    cmd.Parameters.AddWithValue("@hc7", DbInt(item.Hancoin7dPrice));
                    cmd.Parameters.AddWithValue("@hc30", DbInt(item.Hancoin30dPrice));
                    cmd.Parameters.AddWithValue("@hc90", DbInt(item.Hancoin90dPrice));
                    cmd.Parameters.AddWithValue("@hc365", DbInt(item.Hancoin365dPrice));
                    cmd.Parameters.AddWithValue("@hc0", DbInt(item.Hancoin0dPrice));
                    cmd.Parameters.AddWithValue("@mile7", DbInt(item.Mileage7dPrice));
                    cmd.Parameters.AddWithValue("@mile30", DbInt(item.Mileage30dPrice));
                    cmd.Parameters.AddWithValue("@mile90", DbInt(item.Mileage90dPrice));
                    cmd.Parameters.AddWithValue("@mile365", DbInt(item.Mileage365dPrice));
                    cmd.Parameters.AddWithValue("@mile0", DbInt(item.Mileage0dPrice));
                    cmd.Parameters.AddWithValue("@bonusMito7", ParseInt(item.BonusMito7d));
                    cmd.Parameters.AddWithValue("@bonusMito30", ParseInt(item.BonusMito30d));
                    cmd.Parameters.AddWithValue("@bonusMito90", ParseInt(item.BonusMito90d));
                    cmd.Parameters.AddWithValue("@bonusMito365", ParseInt(item.BonusMito365d));
                    cmd.Parameters.AddWithValue("@bonusMito0", ParseInt(item.BonusMito0d));
                    cmd.Parameters.AddWithValue("@bonusSpeed", ParseInt(item.BonusSpeed));
                    cmd.Parameters.AddWithValue("@bonusCrash", ParseInt(item.BonusCrash));
                    cmd.Parameters.AddWithValue("@bonusAccel", ParseInt(item.BonusAccel));
                    cmd.Parameters.AddWithValue("@bonusBoost", ParseInt(item.BonusBoost));
                    cmd.Parameters.AddWithValue("@bonusAssist", ParseInt(item.BonusAssist));
                    cmd.ExecuteNonQuery();
                }
                synchronized++;
            }

            Log.Info("Visual shop catalog synchronized with {0} entries in dbo.visual_item_catalog.", synchronized);
        }

        public static PurchaseResult Purchase(MySqlConnection conn, ulong characterId, uint carId, uint shopId,
            int period, bool useMileage, long clientCash, string data)
        {
            var result = new PurchaseResult { ShopId = shopId, CarId = carId, Period = period };
            if (period < 0 || period > 5)
            {
                result.Error = "invalid_period";
                return result;
            }

            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var catalog = LoadCatalogForPurchase(conn, tx, shopId);
                    if (catalog == null)
                        return FailAndRollback(tx, result, "visual_item_not_found");

                    result.CategoryIndex = catalog.CategoryIndex;
                    result.Support = catalog.Support;
                    var equipable = IsEquipableCategory(catalog.CategoryIndex);

                    if (equipable && carId == 0)
                        return FailAndRollback(tx, result, "invalid_car");

                    if (equipable && !OwnsCar(conn, tx, characterId, carId))
                        return FailAndRollback(tx, result, "not_your_car");

                    CurrencyType currency;
                    int price;
                    int bonusMito;
                    if (!TryResolvePrice(catalog, period, useMileage, out currency, out price, out bonusMito))
                        return FailAndRollback(tx, result, "visual_price_not_configured");

                    result.Currency = currency;
                    result.Price = price;
                    result.BonusMito = bonusMito;

                    long mito;
                    long hancoin;
                    long mileage;
                    if (!LoadBalances(conn, tx, characterId, out mito, out hancoin, out mileage))
                        return FailAndRollback(tx, result, "character_not_found");

                    if ((currency == CurrencyType.Mito && mito < price) ||
                        (currency == CurrencyType.Hancoin && hancoin < price) ||
                        (currency == CurrencyType.Mileage && mileage < price))
                        return FailAndRollback(tx, result, "not_enough_money");

                    var inventoryIndex = NextInventoryIndex(conn, tx, characterId);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var expire = CalculateExpireTime(now, period);

                    if (equipable)
                    {
                        using (var unequip = new MySqlCommand(@"
UPDATE dbo.visual_items
   SET ItemState=0, UpdateTime=@now
 WHERE CharacterId=@cid AND CarId=@carId AND CategoryIndex=@category AND ItemState=1;", conn, tx))
                        {
                            unequip.Parameters.AddWithValue("@now", now);
                            unequip.Parameters.AddWithValue("@cid", characterId);
                            unequip.Parameters.AddWithValue("@carId", carId);
                            unequip.Parameters.AddWithValue("@category", catalog.CategoryIndex);
                            unequip.ExecuteNonQuery();
                        }
                    }

                    using (var insert = new MySqlCommand(@"
INSERT INTO dbo.visual_items
(CharacterId,CarId,ShopId,InventoryIndex,CategoryIndex,Support,ItemState,Data,Period,CreateTime,UpdateTime,ExpireTime,CurrencyType,PaidMito,PaidHancoin,PaidMileage)
VALUES
(@cid,@carId,@shopId,@inven,@category,@support,@state,@data,@period,@now,@now,@expire,@currency,@mito,@hancoin,@mileage);
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", conn, tx))
                    {
                        insert.Parameters.AddWithValue("@cid", characterId);
                        insert.Parameters.AddWithValue("@carId", carId);
                        insert.Parameters.AddWithValue("@shopId", shopId);
                        insert.Parameters.AddWithValue("@inven", inventoryIndex);
                        insert.Parameters.AddWithValue("@category", catalog.CategoryIndex);
                        insert.Parameters.AddWithValue("@support", catalog.Support);
                        insert.Parameters.AddWithValue("@state", equipable ? 1 : 0);
                        insert.Parameters.AddWithValue("@data", string.IsNullOrEmpty(data) ? (object)DBNull.Value : data);
                        insert.Parameters.AddWithValue("@period", period);
                        insert.Parameters.AddWithValue("@now", now);
                        insert.Parameters.AddWithValue("@expire", expire);
                        insert.Parameters.AddWithValue("@currency", (int)currency);
                        insert.Parameters.AddWithValue("@mito", currency == CurrencyType.Mito ? price : 0);
                        insert.Parameters.AddWithValue("@hancoin", currency == CurrencyType.Hancoin ? price : 0);
                        insert.Parameters.AddWithValue("@mileage", currency == CurrencyType.Mileage ? price : 0);
                        insert.ExecuteScalar();
                    }

                    using (var debit = new MySqlCommand(@"
UPDATE dbo.characters SET
    Mito = CASE WHEN @currency=0 THEN Mito-@price ELSE Mito END,
    Hancoin = CASE WHEN @currency=1 THEN Hancoin-@price ELSE Hancoin END,
    Mileage = CASE WHEN @currency=2 THEN Mileage-@price ELSE Mileage END
WHERE CID=@cid;", conn, tx))
                    {
                        debit.Parameters.AddWithValue("@currency", (int)currency);
                        debit.Parameters.AddWithValue("@price", price);
                        debit.Parameters.AddWithValue("@cid", characterId);
                        debit.ExecuteNonQuery();
                    }

                    tx.Commit();
                    result.Success = true;
                    result.InventoryIndex = inventoryIndex;
                    result.Equipped = equipable;

                    Log.Info("VisualShop purchase: CID={0} ShopId={1} CarId={2} Category={3} InvenIdx={4} Currency={5} Price={6} ClientCash={7} Period={8} Equipped={9}",
                        characterId, shopId, carId, catalog.CategoryIndex, inventoryIndex, currency, price, clientCash, period, equipable);
                    return result;
                }
                catch (Exception ex)
                {
                    try { tx.Rollback(); } catch { }
                    result.Error = "visual_purchase_db_error";
                    Log.Error("VisualShop purchase failed for CID={0} ShopId={1}: {2}", characterId, shopId, ex);
                    return result;
                }
            }
        }

        public static List<VisualInventoryRow> LoadInventory(MySqlConnection conn, ulong characterId)
        {
            var rows = new List<VisualInventoryRow>();
            const string sql = @"
SELECT v.Id,v.CharacterId,v.CarId,v.ShopId,v.InventoryIndex,v.CategoryIndex,v.Support,v.ItemState,
       v.Data,v.Period,v.CreateTime,v.UpdateTime,v.ExpireTime,
       COALESCE(c.ServerBonusSpeed,c.SourceBonusSpeed,0) AS BonusSpeed,
       COALESCE(c.ServerBonusCrash,c.SourceBonusCrash,0) AS BonusCrash,
       COALESCE(c.ServerBonusAccel,c.SourceBonusAccel,0) AS BonusAccel,
       COALESCE(c.ServerBonusBoost,c.SourceBonusBoost,0) AS BonusBoost
FROM dbo.visual_items v
LEFT JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
WHERE v.CharacterId=@cid
ORDER BY v.InventoryIndex;";
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) rows.Add(ReadInventoryRow(r));
                }
            }
            return rows;
        }

        public static VisualStatBonus LoadEquippedStatBonus(MySqlConnection conn, ulong characterId, uint carId)
        {
            var result = new VisualStatBonus();
            const string sql = @"
SELECT
 COALESCE(SUM(COALESCE(c.ServerBonusSpeed,c.SourceBonusSpeed,0)),0),
 COALESCE(SUM(COALESCE(c.ServerBonusCrash,c.SourceBonusCrash,0)),0),
 COALESCE(SUM(COALESCE(c.ServerBonusAccel,c.SourceBonusAccel,0)),0),
 COALESCE(SUM(COALESCE(c.ServerBonusBoost,c.SourceBonusBoost,0)),0)
FROM dbo.visual_items v
JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
WHERE v.CharacterId=@cid AND v.CarId=@carId AND v.ItemState=1
  AND (v.ExpireTime=0 OR v.ExpireTime>@now);";
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@carId", carId);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        result.Speed = Convert.ToInt32(r.GetValue(0));
                        result.Crash = Convert.ToInt32(r.GetValue(1));
                        result.Accel = Convert.ToInt32(r.GetValue(2));
                        result.Boost = Convert.ToInt32(r.GetValue(3));
                    }
                }
            }
            return result;
        }

        private static VisualInventoryRow ReadInventoryRow(IDataRecord r)
        {
            return new VisualInventoryRow
            {
                Id = Convert.ToInt64(r[0]),
                CharacterId = unchecked((ulong)Convert.ToInt64(r[1])),
                CarId = unchecked((uint)Convert.ToInt64(r[2])),
                ShopId = unchecked((uint)Convert.ToInt32(r[3])),
                InventoryIndex = unchecked((uint)Convert.ToInt32(r[4])),
                CategoryIndex = Convert.ToInt32(r[5]),
                Support = Convert.ToInt32(r[6]),
                ItemState = Convert.ToInt32(r[7]),
                Data = r.IsDBNull(8) ? string.Empty : Convert.ToString(r[8]),
                Period = Convert.ToInt32(r[9]),
                CreateTime = Convert.ToInt64(r[10]),
                UpdateTime = Convert.ToInt64(r[11]),
                ExpireTime = Convert.ToInt64(r[12]),
                BonusSpeed = Convert.ToInt32(r[13]),
                BonusCrash = Convert.ToInt32(r[14]),
                BonusAccel = Convert.ToInt32(r[15]),
                BonusBoost = Convert.ToInt32(r[16])
            };
        }

        private static CatalogRow LoadCatalogForPurchase(MySqlConnection conn, MySqlTransaction tx, uint shopId)
        {
            const string sql = @"
SELECT ShopId,CategoryIndex,Support,UseMito,UseHancoin,UseMileage,
 COALESCE(ServerMitoPrice,SourceMitoPrice),COALESCE(ServerMito7dPrice,SourceMito7dPrice),
 COALESCE(ServerMito30dPrice,SourceMito30dPrice),COALESCE(ServerMito90dPrice,SourceMito90dPrice),
 COALESCE(ServerMito365dPrice,SourceMito365dPrice),COALESCE(ServerMito0dPrice,SourceMito0dPrice),
 COALESCE(ServerHancoin7dPrice,SourceHancoin7dPrice),COALESCE(ServerHancoin30dPrice,SourceHancoin30dPrice),
 COALESCE(ServerHancoin90dPrice,SourceHancoin90dPrice),COALESCE(ServerHancoin365dPrice,SourceHancoin365dPrice),
 COALESCE(ServerHancoin0dPrice,SourceHancoin0dPrice),
 COALESCE(ServerMileage7dPrice,SourceMileage7dPrice),COALESCE(ServerMileage30dPrice,SourceMileage30dPrice),
 COALESCE(ServerMileage90dPrice,SourceMileage90dPrice),COALESCE(ServerMileage365dPrice,SourceMileage365dPrice),
 COALESCE(ServerMileage0dPrice,SourceMileage0dPrice),
 SourceBonusMito7d,SourceBonusMito30d,SourceBonusMito90d,SourceBonusMito365d,SourceBonusMito0d
FROM dbo.visual_item_catalog WITH (UPDLOCK,HOLDLOCK)
WHERE ShopId=@shopId;";
            using (var cmd = new MySqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@shopId", shopId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new CatalogRow
                    {
                        ShopId = Convert.ToInt32(r[0]), CategoryIndex = Convert.ToInt32(r[1]), Support = Convert.ToInt32(r[2]),
                        UseMito = Convert.ToBoolean(r[3]), UseHancoin = Convert.ToBoolean(r[4]), UseMileage = Convert.ToBoolean(r[5]),
                        MitoPrice = NullableInt(r,6), Mito7 = NullableInt(r,7), Mito30 = NullableInt(r,8), Mito90 = NullableInt(r,9), Mito365 = NullableInt(r,10), Mito0 = NullableInt(r,11),
                        Hc7 = NullableInt(r,12), Hc30 = NullableInt(r,13), Hc90 = NullableInt(r,14), Hc365 = NullableInt(r,15), Hc0 = NullableInt(r,16),
                        Mileage7 = NullableInt(r,17), Mileage30 = NullableInt(r,18), Mileage90 = NullableInt(r,19), Mileage365 = NullableInt(r,20), Mileage0 = NullableInt(r,21),
                        BonusMito7 = Convert.ToInt32(r[22]), BonusMito30 = Convert.ToInt32(r[23]), BonusMito90 = Convert.ToInt32(r[24]), BonusMito365 = Convert.ToInt32(r[25]), BonusMito0 = Convert.ToInt32(r[26])
                    };
                }
            }
        }

        private static bool TryResolvePrice(CatalogRow row, int period, bool requestedMileage, out CurrencyType currency, out int price, out int bonusMito)
        {
            currency = requestedMileage ? CurrencyType.Mileage : (row.UseHancoin ? CurrencyType.Hancoin : CurrencyType.Mito);
            price = 0;
            bonusMito = 0;
            if (requestedMileage && !row.UseMileage) return false;
            if (!requestedMileage && currency == CurrencyType.Hancoin && !row.UseHancoin) return false;
            if (!requestedMileage && currency == CurrencyType.Mito && !row.UseMito) return false;

            int? selected = null;
            switch (currency)
            {
                case CurrencyType.Mito:
                    if (period == 0) selected = row.MitoPrice;
                    else if (period == 1) selected = row.Mito7;
                    else if (period == 2) selected = row.Mito30;
                    else if (period == 3) selected = row.Mito90 ?? row.Mito365;
                    else if (period == 4) selected = row.Mito0;
                    else if (period == 5) selected = row.MitoPrice ?? row.Mito0;
                    break;
                case CurrencyType.Hancoin:
                    if (period == 1) selected = row.Hc7;
                    else if (period == 2) selected = row.Hc30;
                    else if (period == 3) selected = row.Hc90 ?? row.Hc365;
                    else if (period == 4 || period == 5) selected = row.Hc0;
                    break;
                case CurrencyType.Mileage:
                    if (period == 1) selected = row.Mileage7;
                    else if (period == 2) selected = row.Mileage30;
                    else if (period == 3) selected = row.Mileage90 ?? row.Mileage365;
                    else if (period == 4 || period == 5) selected = row.Mileage0;
                    break;
            }
            if (!selected.HasValue || selected.Value < 0) return false;
            price = selected.Value;
            if (period == 1) bonusMito = row.BonusMito7;
            else if (period == 2) bonusMito = row.BonusMito30;
            else if (period == 3) bonusMito = row.BonusMito90;
            else if (period == 4 || period == 5) bonusMito = row.BonusMito0;
            return true;
        }

        private static bool OwnsCar(MySqlConnection conn, MySqlTransaction tx, ulong characterId, uint carId)
        {
            using (var cmd = new MySqlCommand("SELECT COUNT(1) FROM dbo.vehicles WITH (UPDLOCK,HOLDLOCK) WHERE CID=@carId AND CharID=@cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@carId", carId);
                cmd.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        private static bool LoadBalances(MySqlConnection conn, MySqlTransaction tx, ulong characterId, out long mito, out long hancoin, out long mileage)
        {
            mito = hancoin = mileage = 0;
            using (var cmd = new MySqlCommand("SELECT Mito,Hancoin,Mileage FROM dbo.characters WITH (UPDLOCK,HOLDLOCK) WHERE CID=@cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return false;
                    mito = r.IsDBNull(0) ? 0 : Convert.ToInt64(r[0]);
                    hancoin = r.IsDBNull(1) ? 0 : Convert.ToInt64(r[1]);
                    mileage = r.IsDBNull(2) ? 0 : Convert.ToInt64(r[2]);
                    return true;
                }
            }
        }

        private static uint NextInventoryIndex(MySqlConnection conn, MySqlTransaction tx, ulong characterId)
        {
            using (var cmd = new MySqlCommand("SELECT COALESCE(MAX(InventoryIndex),-1)+1 FROM dbo.visual_items WITH (UPDLOCK,HOLDLOCK) WHERE CharacterId=@cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                return unchecked((uint)Convert.ToInt32(cmd.ExecuteScalar()));
            }
        }

        public static bool IsEquipableCategory(int categoryIndex)
        {
            return categoryIndex > 0 && categoryIndex != 16 && categoryIndex != 19 && categoryIndex != 22;
        }

        private static long CalculateExpireTime(long now, int period)
        {
            if (period == 1) return now + (7L * 86400L);
            if (period == 2) return now + (30L * 86400L);
            if (period == 3) return now + (90L * 86400L);
            return 0;
        }

        private static PurchaseResult FailAndRollback(MySqlTransaction tx, PurchaseResult result, string error)
        {
            result.Error = error;
            tx.Rollback();
            return result;
        }

        private static int? NullableInt(IDataRecord r, int ordinal)
        {
            return r.IsDBNull(ordinal) ? (int?)null : Convert.ToInt32(r[ordinal]);
        }

        public static int ParseInt(string value)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static bool ParseBool(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static object DbInt(string value)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? (object)result : DBNull.Value;
        }

        private static object DbString(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        }
    }
}
