using System;
using System.Globalization;
using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
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

                // Some retail paint rows (notably i_g_paint_S15) are malformed in the
                // XLT: their price/useMito cells are empty although the retail UI sells
                // them. Repair only the Server* override and retry the transaction.
                if (!purchase.Success && purchase.Error == "visual_price_not_configured" &&
                    VisualShopCatalogRecovery.TryInferMissingMitoPrice(
                        conn, request.TableIndex, unchecked((int)request.PeriodIdx)))
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
                }

                // Some retail VShop rows are zero-price helper entries. They are still
                // valid customization rows, but only support=0 entries may use this path.
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
                    // Repair only legacy CategoryIndex values. Do NOT recompute ItemState
                    // for unrelated categories: doing that re-equipped visuals the player
                    // had deliberately unequipped whenever another visual was purchased.
                    NormalizeVisualCategories(conn, character.Id, request.CarId);
                    purchase.Equipped = purchase.CategoryIndex > 0;
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

            // Retail sends Cmd_VSItemModList (1202), not a complete VisualItemListAck
            // (1201), after a visual mutation. Sending the affected category is important
            // because purchase can implicitly unequip the previous item in that category.
            if (purchase.CategoryIndex > 0)
                VisualShopProtocolSync.SendCategory(packet, character.Id, purchase.CarId, purchase.CategoryIndex);
            else
                VisualShopProtocolSync.SendInventory(packet, character.Id, purchase.InventoryIndex);

            // Original v0.77 flow sends VSItemModList before BuyVisualItemAck.
            var ack = new BuyVisualItemThreadAnswer
            {
                Type = 0,
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
                VisualShopWorldSync.Broadcast(user);

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

            int support;
            int category;
            using (var catalog = new MySqlCommand(@"
SELECT Support, CategoryIndex
FROM dbo.visual_item_catalog
WHERE ShopId=@shopId;", conn))
            {
                catalog.Parameters.AddWithValue("@shopId", shopId);
                using (var reader = catalog.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        result.Error = "visual_item_not_found";
                        return result;
                    }

                    support = Convert.ToInt32(reader[0], CultureInfo.InvariantCulture);
                    category = Convert.ToInt32(reader[1], CultureInfo.InvariantCulture);
                }
            }

            if (support != 0)
            {
                result.Error = "visual_price_not_configured";
                return result;
            }

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
                "VisualShop zero-price helper persisted: CID={0} ShopId={1} CarId={2} CategoryIndex={3} InvenIdx={4}",
                characterId, shopId, carId, category, inventoryIndex);
            return result;
        }

        private static void NormalizeVisualCategories(MySqlConnection conn, ulong characterId, uint carId)
        {
            using (var cmd = new MySqlCommand(@"
UPDATE v
SET CategoryIndex=c.CategoryIndex
FROM dbo.visual_items v
JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
WHERE v.CharacterId=@cid AND v.CarId=@carId
  AND c.CategoryIndex > 0
  AND v.CategoryIndex<>c.CategoryIndex;", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@carId", carId);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
