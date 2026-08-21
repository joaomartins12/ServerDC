using System;
using System.Globalization;
using System.Linq;
using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class BuyVisualItemThread
    {
        [Packet(Packets.CmdBuyVisualItemThread)]
        public static void Handle(Packet packet)
        {
            var request = new BuyVisualItemThreadPacket(packet);
            var user = packet.Sender.User;
            var character = user == null ? null : user.ActiveCharacter;
            if (character == null)
            {
                packet.Sender.SendError("Failed to purchase item!");
                return;
            }

            VisualShopDatabase.PurchaseResult purchase;
            using (var conn = GameServer.Instance.Database.Connection)
            {
                purchase = VisualShopDatabase.Purchase(
                    conn,
                    character.Id,
                    request.CarId,
                    request.TableIndex,
                    unchecked((int)request.PeriodIdx),
                    request.UseMileage,
                    request.Cash,
                    request.PlateName);

                // Some visual customization rows are intentionally zero-priced helper
                // entries (support=0). The original server accepts them as part of the
                // customization bundle. Do not reject those as an unconfigured price.
                if (!purchase.Success && purchase.Error == "visual_price_not_configured")
                {
                    purchase = TryPersistZeroPriceVisual(
                        conn,
                        character.Id,
                        request.CarId,
                        request.TableIndex,
                        unchecked((int)request.PeriodIdx),
                        request.PlateName);
                }

                if (purchase.Success)
                {
                    // VShopItems.xml exposes both category and categoryIdx. category is
                    // the visual equipment family; categoryIdx is an index inside that
                    // family. The old implementation used categoryIdx, which produced
                    // Category=0 for valid spoilers/decals and therefore never equipped
                    // them. Normalize both the new row and rows bought earlier in this
                    // session from the authoritative catalog category.
                    NormalizeVisualCategories(conn, character.Id, request.CarId);
                    purchase.Equipped = IsEquipableVisual(request.TableIndex);
                }
            }

            if (!purchase.Success)
            {
                Log.Warning(
                    "BuyVisualItem rejected: CID={0} ShopId={1} CarId={2} Period={3} Mileage={4} ClientCash={5} Reason={6}",
                    character.Id,
                    request.TableIndex,
                    request.CarId,
                    request.PeriodIdx,
                    request.UseMileage,
                    request.Cash,
                    purchase.Error ?? "unknown");
                packet.Sender.SendError("Failed to purchase item!");
                return;
            }

            // The SQL transaction is authoritative. Keep the already-loaded character
            // snapshot in sync so subsequent packets in this session show the new balance.
            switch (purchase.Currency)
            {
                case VisualShopDatabase.CurrencyType.Mito:
                    character.MitoMoney -= purchase.Price;
                    break;
                case VisualShopDatabase.CurrencyType.Hancoin:
                    character.Hancoin -= purchase.Price;
                    break;
                case VisualShopDatabase.CurrencyType.Mileage:
                    character.TotalDistance -= purchase.Price;
                    break;
            }

            var ack = new BuyVisualItemThreadAnswer
            {
                Type = purchase.Support,
                TableIndex = purchase.ShopId,
                CarId = purchase.CarId,
                InventoryId = unchecked((int)purchase.InventoryIndex),
                Period = purchase.Period,
                Mito = purchase.Currency == VisualShopDatabase.CurrencyType.Mito ? purchase.Price : 0,
                Hancoin = purchase.Currency == VisualShopDatabase.CurrencyType.Hancoin ? purchase.Price : 0,
                BonusMito = purchase.BonusMito,
                Mileage = purchase.Currency == VisualShopDatabase.CurrencyType.Mileage ? purchase.Price : 0
            };
            packet.Sender.Send(ack.CreatePacket());

            if (purchase.Equipped)
            {
                var visual = PlayerVisualSnapshotBuilder.BuildRoomNotifyChange(user.VehicleSerial, character);
                packet.Sender.Send(visual.CreatePacket());
            }

            CheckStat.Handle(packet);
        }

        private static VisualShopDatabase.PurchaseResult TryPersistZeroPriceVisual(
            MySqlConnection conn,
            ulong characterId,
            uint carId,
            uint shopId,
            int period,
            string data)
        {
            var result = new VisualShopDatabase.PurchaseResult
            {
                Success = false,
                ShopId = shopId,
                CarId = carId,
                Period = period,
                Currency = VisualShopDatabase.CurrencyType.Mito,
                Price = 0
            };

            var source = ServerMain.VisualItems == null
                ? null
                : ServerMain.VisualItems.FirstOrDefault(x => ParseUInt(x.UniqueId) == shopId);
            if (source == null)
            {
                result.Error = "visual_item_not_found";
                return result;
            }

            int support;
            int.TryParse(source.Support, NumberStyles.Integer, CultureInfo.InvariantCulture, out support);
            if (support != 0)
            {
                result.Error = "visual_price_not_configured";
                return result;
            }

            // Verify car ownership again because this fallback bypasses Purchase().
            using (var own = new MySqlCommand(
                "SELECT COUNT(1) FROM dbo.vehicles WHERE CID=@carId AND CharID=@charId", conn))
            {
                own.Parameters.AddWithValue("@carId", carId);
                own.Parameters.AddWithValue("@charId", characterId);
                if (Convert.ToInt32(own.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    result.Error = "visual_car_not_owned";
                    return result;
                }
            }

            var category = ParseCategory(source.Category);
            uint inventoryIndex;
            using (var next = new MySqlCommand(@"
SELECT ISNULL(MAX(InventoryIndex),-1)+1
FROM dbo.visual_items
WHERE CharacterId=@cid;", conn))
            {
                next.Parameters.AddWithValue("@cid", characterId);
                inventoryIndex = unchecked((uint)Convert.ToInt32(next.ExecuteScalar(), CultureInfo.InvariantCulture));
            }

            if (category > 0)
            {
                using (var unequip = new MySqlCommand(@"
UPDATE dbo.visual_items
SET ItemState=0, UpdateTime=@now
WHERE CharacterId=@cid AND CarId=@carId AND CategoryIndex=@category AND ItemState=1;", conn))
                {
                    unequip.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    unequip.Parameters.AddWithValue("@cid", characterId);
                    unequip.Parameters.AddWithValue("@carId", carId);
                    unequip.Parameters.AddWithValue("@category", category);
                    unequip.ExecuteNonQuery();
                }
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var insert = new MySqlCommand(@"
INSERT INTO dbo.visual_items
(CharacterId,CarId,ShopId,InventoryIndex,CategoryIndex,Support,ItemState,Data,Period,CreateTime,UpdateTime,ExpireTime,CurrencyType,PaidMito,PaidHancoin,PaidMileage)
VALUES
(@cid,@carId,@shopId,@inven,@category,@support,@state,@data,@period,@now,@now,0,0,0,0,0);", conn))
            {
                insert.Parameters.AddWithValue("@cid", characterId);
                insert.Parameters.AddWithValue("@carId", carId);
                insert.Parameters.AddWithValue("@shopId", shopId);
                insert.Parameters.AddWithValue("@inven", inventoryIndex);
                insert.Parameters.AddWithValue("@category", category);
                insert.Parameters.AddWithValue("@support", support);
                insert.Parameters.AddWithValue("@state", category > 0 ? 1 : 0);
                insert.Parameters.AddWithValue("@data", string.IsNullOrWhiteSpace(data) ? (object)DBNull.Value : data.Trim());
                insert.Parameters.AddWithValue("@period", period);
                insert.Parameters.AddWithValue("@now", now);
                insert.ExecuteNonQuery();
            }

            result.Success = true;
            result.InventoryIndex = inventoryIndex;
            result.CategoryIndex = category;
            result.Support = support;
            result.Equipped = category > 0;

            Log.Info(
                "VisualShop zero-price helper persisted: CID={0} ShopId={1} CarId={2} Category={3} InvenIdx={4}",
                characterId, shopId, carId, category, inventoryIndex);
            return result;
        }

        private static void NormalizeVisualCategories(MySqlConnection conn, ulong characterId, uint carId)
        {
            using (var cmd = new MySqlCommand(@"
;WITH normalized AS
(
    SELECT v.Id,
           v.InventoryIndex,
           TRY_CONVERT(INT,c.Category) AS RealCategory,
           ROW_NUMBER() OVER
           (
               PARTITION BY TRY_CONVERT(INT,c.Category)
               ORDER BY v.InventoryIndex DESC, v.Id DESC
           ) AS rn
    FROM dbo.visual_items v
    JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
    WHERE v.CharacterId=@cid AND v.CarId=@carId
      AND TRY_CONVERT(INT,c.Category) IS NOT NULL
      AND TRY_CONVERT(INT,c.Category) > 0
)
UPDATE v
SET CategoryIndex=n.RealCategory,
    ItemState=CASE WHEN n.rn=1 THEN 1 ELSE 0 END
FROM dbo.visual_items v
JOIN normalized n ON n.Id=v.Id;", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@carId", carId);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool IsEquipableVisual(uint shopId)
        {
            var source = ServerMain.VisualItems == null
                ? null
                : ServerMain.VisualItems.FirstOrDefault(x => ParseUInt(x.UniqueId) == shopId);
            return source != null && ParseCategory(source.Category) > 0;
        }

        private static int ParseCategory(string value)
        {
            int category;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out category)
                ? category
                : 0;
        }

        private static uint ParseUInt(string value)
        {
            uint parsed;
            return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0u;
        }
    }
}
