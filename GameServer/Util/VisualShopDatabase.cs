using System;
using System.Collections.Generic;
using System.Globalization;
using Shared.Models;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    /// <summary>
    /// SQL Server backing store for the visual shop.
    ///
    /// The client supplied TableIndex/Cash values are never used as an authoritative
    /// price source. VShopItems.xml is synchronized into dbo.visual_item_catalog and
    /// purchases resolve their price from that server-side table.
    ///
    /// Server* price columns and Bonus* columns are deliberately preserved on sync so
    /// they can be corrected/overridden in SSMS without editing the client files.
    /// </summary>
    public static class VisualShopDatabase
    {
        public const string CatalogTable = "dbo.visual_item_catalog";
        public const string InventoryTable = "dbo.visual_items";

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

        BonusSpeed INT NOT NULL CONSTRAINT DF_visual_item_catalog_BonusSpeed DEFAULT (0),
        BonusCrash INT NOT NULL CONSTRAINT DF_visual_item_catalog_BonusCrash DEFAULT (0),
        BonusAccel INT NOT NULL CONSTRAINT DF_visual_item_catalog_BonusAccel DEFAULT (0),
        BonusBoost INT NOT NULL CONSTRAINT DF_visual_item_catalog_BonusBoost DEFAULT (0),
        BonusGrade INT NOT NULL CONSTRAINT DF_visual_item_catalog_BonusGrade DEFAULT (0),
        BonusMP INT NOT NULL CONSTRAINT DF_visual_item_catalog_BonusMP DEFAULT (0),
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
END;";

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
    UPDATE dbo.visual_item_catalog
       SET ItemCode=@itemCode,
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
           UpdatedUtc=SYSUTCDATETIME()
     WHERE ShopId=@shopId;
END
ELSE
BEGIN
    INSERT INTO dbo.visual_item_catalog
    (ShopId,ItemCode,Category,CategoryIndex,Support,UseMito,UseHancoin,UseMileage,
     SourceMitoPrice,SourceMito7dPrice,SourceMito30dPrice,SourceMito90dPrice,SourceMito365dPrice,SourceMito0dPrice,
     SourceHancoin7dPrice,SourceHancoin30dPrice,SourceHancoin90dPrice,SourceHancoin365dPrice,SourceHancoin0dPrice)
    VALUES
    (@shopId,@itemCode,@category,@categoryIndex,@support,@useMito,@useHancoin,@useMileage,
     @mito,@mito7,@mito30,@mito90,@mito365,@mito0,@hc7,@hc30,@hc90,@hc365,@hc0);
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
                    cmd.Parameters.AddWithValue("@useMileage", ParseInt(item.Mileage) != 0);
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
                    cmd.ExecuteNonQuery();
                }
                synchronized++;
            }

            Log.Info("Visual shop catalog synchronized with {0} entries in dbo.visual_item_catalog.", synchronized);
        }

        public static int ParseInt(string value)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static bool ParseBool(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static object DbInt(string value)
        {
            int result;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return result;
            return DBNull.Value;
        }

        private static object DbString(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        }
    }
}
