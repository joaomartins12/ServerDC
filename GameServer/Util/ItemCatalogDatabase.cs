using System;
using System.Collections.Generic;
using System.Globalization;
using Shared.Models;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    /// <summary>
    /// Keeps a server-side master catalog of every runtime item TableIndex.
    /// The source metadata is refreshed from Items.xml / UseItems.xml at startup,
    /// while administrative fields (IsEnabled and Server*Price) are preserved.
    /// </summary>
    public static class ItemCatalogDatabase
    {
        public static void Synchronize(MySqlConnection connection, IList<BasicItem> items)
        {
            if (connection == null || items == null)
                return;

            EnsureTable(connection);

            using (var tx = connection.BeginTransaction())
            {
                try
                {
                    for (var tableIndex = 0; tableIndex < items.Count; tableIndex++)
                    {
                        var item = items[tableIndex];
                        if (item == null)
                            continue;

                        var part = item as ItemTable.Item;
                        var use = item as UseItemTable.UseItem;

                        const string sql = @"
MERGE dbo.item_catalog AS target
USING (SELECT @TableIndex AS TableIndex) AS source
ON target.TableIndex = source.TableIndex
WHEN MATCHED THEN
    UPDATE SET
        ItemId = @ItemId,
        SourceType = @SourceType,
        Name = @Name,
        Description = @Description,
        Category = @Category,
        FunctionName = @FunctionName,
        NextState = @NextState,
        SourceBuyValue = @SourceBuyValue,
        SourceSellValue = @SourceSellValue,
        SourceBuyPrice = @SourceBuyPrice,
        SourceSellPrice = @SourceSellPrice,
        ExpirationTime = @ExpirationTime,
        Auctionable = @Auctionable,
        PartsShop = @PartsShop,
        Sendable = @Sendable,
        Stackable = @Stackable,
        MaxStack = @MaxStack,
        Grade = @Grade,
        RequiredLevel = @RequiredLevel,
        BasePoints = @BasePoints,
        BasePointModifier = @BasePointModifier,
        BasePointVariable = @BasePointVariable,
        PartAssist = @PartAssist,
        Lube = @Lube,
        NeoStats = @NeoStats,
        StatModifier = @StatModifier,
        Cooldown = @Cooldown,
        Duration = @Duration,
        SourceUpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT
    (
        TableIndex, ItemId, SourceType, Name, Description, Category, FunctionName, NextState,
        SourceBuyValue, SourceSellValue, SourceBuyPrice, SourceSellPrice, ExpirationTime,
        Auctionable, PartsShop, Sendable, Stackable, MaxStack, Grade, RequiredLevel,
        BasePoints, BasePointModifier, BasePointVariable, PartAssist, Lube, NeoStats,
        StatModifier, Cooldown, Duration, IsEnabled, SourceUpdatedAt
    )
    VALUES
    (
        @TableIndex, @ItemId, @SourceType, @Name, @Description, @Category, @FunctionName, @NextState,
        @SourceBuyValue, @SourceSellValue, @SourceBuyPrice, @SourceSellPrice, @ExpirationTime,
        @Auctionable, @PartsShop, @Sendable, @Stackable, @MaxStack, @Grade, @RequiredLevel,
        @BasePoints, @BasePointModifier, @BasePointVariable, @PartAssist, @Lube, @NeoStats,
        @StatModifier, @Cooldown, @Duration, 1, SYSUTCDATETIME()
    );";

                        using (var cmd = new MySqlCommand(sql, connection, tx))
                        {
                            cmd.Parameters.AddWithValue("@TableIndex", tableIndex);
                            cmd.Parameters.AddWithValue("@ItemId", DbText(item.Id));
                            cmd.Parameters.AddWithValue("@SourceType", part != null ? "Item" : use != null ? "UseItem" : "BasicItem");
                            cmd.Parameters.AddWithValue("@Name", DbText(item.Name));
                            cmd.Parameters.AddWithValue("@Description", DbText(item.Description));
                            cmd.Parameters.AddWithValue("@Category", DbText(item.Category));
                            cmd.Parameters.AddWithValue("@FunctionName", DbText(item.Function));
                            cmd.Parameters.AddWithValue("@NextState", DbText(item.NextState));
                            cmd.Parameters.AddWithValue("@SourceBuyValue", DbText(item.BuyValue));
                            cmd.Parameters.AddWithValue("@SourceSellValue", DbText(item.SellValue));
                            cmd.Parameters.AddWithValue("@SourceBuyPrice", DbInt(item.BuyValue));
                            cmd.Parameters.AddWithValue("@SourceSellPrice", DbInt(item.SellValue));
                            cmd.Parameters.AddWithValue("@ExpirationTime", DbText(item.ExpirationTime));
                            cmd.Parameters.AddWithValue("@Auctionable", DbBool(item.Auctionable));
                            cmd.Parameters.AddWithValue("@PartsShop", DbBool(item.PartsShop));
                            cmd.Parameters.AddWithValue("@Sendable", DbBool(item.Sendable));
                            cmd.Parameters.AddWithValue("@Stackable", item.IsStackable());
                            cmd.Parameters.AddWithValue("@MaxStack", SafeMaxStack(item));
                            cmd.Parameters.AddWithValue("@Grade", DbText(part == null ? null : part.Grade));
                            cmd.Parameters.AddWithValue("@RequiredLevel", DbInt(part == null ? null : part.RequiredLevel));
                            cmd.Parameters.AddWithValue("@BasePoints", DbInt(part == null ? null : part.BasePoints));
                            cmd.Parameters.AddWithValue("@BasePointModifier", DbInt(part == null ? null : part.BasePointModifier));
                            cmd.Parameters.AddWithValue("@BasePointVariable", DbInt(part == null ? null : part.BasePointVariable));
                            cmd.Parameters.AddWithValue("@PartAssist", DbText(part == null ? null : part.PartAssist));
                            cmd.Parameters.AddWithValue("@Lube", DbText(part == null ? null : part.Lube));
                            cmd.Parameters.AddWithValue("@NeoStats", DbText(part == null ? null : part.NeoStats));
                            cmd.Parameters.AddWithValue("@StatModifier", DbText(use == null ? null : use.StatModifier));
                            cmd.Parameters.AddWithValue("@Cooldown", DbText(use == null ? null : use.CooldownTime));
                            cmd.Parameters.AddWithValue("@Duration", DbText(use == null ? null : use.Duration));
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                    Log.Info("Item catalog synchronized to database with {0:D} runtime entries", items.Count);
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private static void EnsureTable(MySqlConnection connection)
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

            using (var cmd = new MySqlCommand(sql, connection))
                cmd.ExecuteNonQuery();
        }

        private static object DbText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        }

        private static object DbInt(string value)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? (object)result
                : DBNull.Value;
        }

        private static object DbBool(string value)
        {
            bool result;
            if (bool.TryParse(value, out result))
                return result;
            if (value == "1") return true;
            if (value == "0") return false;
            return DBNull.Value;
        }

        private static object SafeMaxStack(BasicItem item)
        {
            try
            {
                return checked((int)item.GetMaxStack());
            }
            catch
            {
                return DBNull.Value;
            }
        }
    }
}
