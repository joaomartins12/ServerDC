using System;
using MySql.Data.MySqlClient;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class BuyItem
    {
        // Drift City uses a separate protocol namespace for UseItems.xml.
        // Captured client packet for Mittron Fuel (5L): protocol TableIndex=1650.
        // Its zero-based UseItems index is 241, therefore 0x580 + (241 + 1) = 1650.
        private const int UseItemProtocolBase = 0x580;

        [Packet(Packets.CmdBuyItem)]
        public static void Handle(Packet packet)
        {
            var buyItemPacket = new BuyItemPacket(packet);
            var protocolTableIndex = checked((int)buyItemPacket.TableIndex);

            int catalogIndex;
            BasicItem itemData;
            bool isUseItem;
            if (!TryResolveClientTableIndex(protocolTableIndex, out catalogIndex, out itemData, out isUseItem))
            {
                packet.Sender.SendDebugError($"Item protocol TableIndex {protocolTableIndex} could not be resolved!");
#if !DEBUG
                packet.Sender.KillConnection("Invalid shop item");
#endif
                return;
            }

            Log.Info(
                "BuyItem resolve: ProtocolTableIndex={0} -> CatalogIndex={1} Source={2} ItemId={3} Name={4} BuyValue={5} SellValue={6}",
                protocolTableIndex,
                catalogIndex,
                isUseItem ? "UseItem" : "Item",
                itemData.Id,
                itemData.Name,
                itemData.BuyValue,
                itemData.SellValue);

            int unitPrice;
            if (itemData.BuyValue == "n/a" || !int.TryParse(itemData.BuyValue, out unitPrice))
            {
                packet.Sender.SendDebugError($"No price ({itemData.BuyValue}) for item {itemData.Name}");
#if !DEBUG
                packet.Sender.KillConnection("Price missing!");
#endif
                return;
            }

            var price = checked(unitPrice * (int)buyItemPacket.Quantity);

            var character = packet.Sender.User.ActiveCharacter;
            if (character.MitoMoney < price)
            {
                packet.Sender.SendDebugError("Not enough money");
                return;
            }

            // GiveItem needs the merged server catalog index so it can resolve the XML definition.
            // Shop purchases must enter the inventory as completely unequipped items. GiveItem's
            // generic constructor historically associates a new item with the active car; when that
            // CarId is sent to the client before the first equip, the client interprets it as part of
            // the displayed stat (for example CarId 10078 + Small(+1) showed as Speed +10079).
            var inventoryItem = character.GiveItem(
                GameServer.Instance.Database.Connection,
                catalogIndex,
                buyItemPacket.Quantity);
            if (inventoryItem == null)
            {
                packet.Sender.SendDebugError("Giving item failed");
                return;
            }

            var requiresPersist = false;

            // Inventory packets must keep the client's TableIndex, not the server XML runtime index.
            // This matters for normal Items too: current ItemClient.tdf ordering no longer exactly
            // matches Items.xml after the first entries.
            if (inventoryItem.TableIndex != protocolTableIndex)
            {
                var oldIndex = inventoryItem.TableIndex;
                inventoryItem.TableIndex = protocolTableIndex;
                requiresPersist = true;

                Log.Info(
                    "Client item index persisted: DbId={0} InvenIdx={1} CatalogIndex={2} OldTableIndex={3} ProtocolTableIndex={4} ItemId={5} Name={6}",
                    inventoryItem.DbId,
                    inventoryItem.InventoryIndex,
                    catalogIndex,
                    oldIndex,
                    inventoryItem.TableIndex,
                    itemData.Id,
                    itemData.Name);
            }

            // A freshly purchased item is not installed in any vehicle yet.
            // Keep every relationship/state field neutral until CmdEquipItem explicitly assigns it.
            if (inventoryItem.CarId != 0 ||
                inventoryItem.LastCarId != 0 ||
                inventoryItem.State != 0 ||
                inventoryItem.Slot != 0 ||
                inventoryItem.Belonging != 0)
            {
                Log.Debug(
                    "PartShop clearing new item linkage: DbId={0} InvenIdx={1} CarId={2} LastCarId={3} State={4} Slot={5} Belonging={6}",
                    inventoryItem.DbId,
                    inventoryItem.InventoryIndex,
                    inventoryItem.CarId,
                    inventoryItem.LastCarId,
                    inventoryItem.State,
                    inventoryItem.Slot,
                    inventoryItem.Belonging);

                inventoryItem.CarId = 0;
                inventoryItem.LastCarId = 0;
                inventoryItem.State = 0;
                inventoryItem.Slot = 0;
                inventoryItem.Belonging = 0;
                requiresPersist = true;
            }

            if (requiresPersist)
                ItemModel.Update(GameServer.Instance.Database.Connection, inventoryItem);

            Log.Debug(
                "PartShop item instance: DbId={0} InvenIdx={1} TableIndex={2} CarId={3} LastCarId={4} State={5} Slot={6} Belonging={7} Random={8} Upgrade={9} UpgradePoint={10}",
                inventoryItem.DbId,
                inventoryItem.InventoryIndex,
                inventoryItem.TableIndex,
                inventoryItem.CarId,
                inventoryItem.LastCarId,
                inventoryItem.State,
                inventoryItem.Slot,
                inventoryItem.Belonging,
                inventoryItem.Random,
                inventoryItem.Upgrade,
                inventoryItem.UpgradePoint);

            character.MitoMoney -= price;
            CharacterModel.Update(GameServer.Instance.Database.Connection, character);

            var ack = new BuyItemAnswer()
            {
                ItemId = buyItemPacket.TableIndex,
                Quantity = buyItemPacket.Quantity,
                Price = price,
            };
            packet.Sender.Send(ack.CreatePacket());

            character.FlushItemModBuffer(packet.Sender);
        }

        private static bool TryResolveClientTableIndex(
            int protocolTableIndex,
            out int catalogIndex,
            out BasicItem itemData,
            out bool isUseItem)
        {
            catalogIndex = -1;
            itemData = null;
            isUseItem = false;

            if (ServerMain.Items == null || ServerMain.Items.Count == 0 || protocolTableIndex < 0)
                return false;

            var firstUseItemCatalogIndex = FindFirstUseItemCatalogIndex();

            // UseItems shop packets use their historical 0x580 namespace. Keep this path first,
            // because client_item_lookup stores the global inventory index instead.
            if (firstUseItemCatalogIndex >= 0 && protocolTableIndex > UseItemProtocolBase)
            {
                var useItemIndex = protocolTableIndex - UseItemProtocolBase - 1;
                var candidateCatalogIndex = firstUseItemCatalogIndex + useItemIndex;

                if (candidateCatalogIndex >= firstUseItemCatalogIndex &&
                    candidateCatalogIndex < ServerMain.Items.Count &&
                    ServerMain.Items[candidateCatalogIndex] is UseItemTable.UseItem)
                {
                    catalogIndex = candidateCatalogIndex;
                    itemData = ServerMain.Items[candidateCatalogIndex];
                    isUseItem = true;
                    return true;
                }
            }

            // Normal ItemClient.tdf indexes must be translated through the imported client table.
            // ItemClient ordering differs from Items.xml, which previously made Small correct only
            // by coincidence while Intro/Bfemil/Chorus resolved another server item and price.
            string itemId;
            if (TryGetClientItemId(protocolTableIndex, out itemId))
            {
                for (var i = 0; i < ServerMain.Items.Count; i++)
                {
                    if (!(ServerMain.Items[i] is ItemTable.Item))
                        continue;
                    if (!string.Equals(ServerMain.Items[i].Id, itemId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    catalogIndex = i;
                    itemData = ServerMain.Items[i];
                    return true;
                }

                Log.Warning(
                    "BuyItem client lookup resolved TableIndex={0} to ItemId={1}, but that ItemId was not found in Items.xml.",
                    protocolTableIndex,
                    itemId);
            }

            // Compatibility fallback for installations that have not imported ItemClient.tdf yet.
            if (protocolTableIndex < ServerMain.Items.Count &&
                ServerMain.Items[protocolTableIndex] is ItemTable.Item)
            {
                catalogIndex = protocolTableIndex;
                itemData = ServerMain.Items[catalogIndex];
                Log.Warning(
                    "BuyItem using legacy direct index fallback for ClientTableIndex={0} ItemId={1}. Import ItemClient.tdf for authoritative mapping.",
                    protocolTableIndex,
                    itemData.Id);
                return true;
            }

            return false;
        }

        private static bool TryGetClientItemId(int clientTableIndex, out string itemId)
        {
            itemId = null;

            try
            {
                using (var cmd = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.client_item_lookup', N'U') IS NOT NULL
BEGIN
    SELECT TOP (1) ItemId
    FROM dbo.client_item_lookup
    WHERE ClientTableIndex=@tableIndex
      AND (SourceFile LIKE '%ItemClient.tdf' OR SourceTable=N'client_Item');
END", GameServer.Instance.Database.Connection))
                {
                    cmd.Parameters.AddWithValue("@tableIndex", clientTableIndex);
                    var value = cmd.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                        return false;

                    itemId = Convert.ToString(value).Trim();
                    return !string.IsNullOrWhiteSpace(itemId);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("BuyItem client item lookup failed for TableIndex={0}: {1}", clientTableIndex, ex.Message);
                return false;
            }
        }

        private static int FindFirstUseItemCatalogIndex()
        {
            for (var i = 0; i < ServerMain.Items.Count; i++)
            {
                if (ServerMain.Items[i] is UseItemTable.UseItem)
                    return i;
            }

            return -1;
        }
    }
}
