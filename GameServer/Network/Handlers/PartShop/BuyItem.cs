using System;
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
                "BuyItem resolve: ProtocolTableIndex={0} -> CatalogIndex={1} Source={2} ItemId={3} Name={4}",
                protocolTableIndex,
                catalogIndex,
                isUseItem ? "UseItem" : "Item",
                itemData.Id,
                itemData.Name);

            int price;
            if (!int.TryParse(itemData.BuyValue, out price) || itemData.BuyValue == "n/a")
            {
                packet.Sender.SendDebugError($"No price ({itemData.BuyValue}) for item {itemData.Name}");
#if !DEBUG
                packet.Sender.KillConnection("Price missing!");
#endif
                return;
            }

            price = price * (int)buyItemPacket.Quantity;

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

            if (isUseItem && inventoryItem.TableIndex != protocolTableIndex)
            {
                var oldIndex = inventoryItem.TableIndex;
                inventoryItem.TableIndex = protocolTableIndex;
                requiresPersist = true;

                Log.Info(
                    "UseItem protocol index resolved: DbId={0} InvenIdx={1} CatalogIndex={2} OldTableIndex={3} ProtocolTableIndex={4} ItemId={5} Name={6}",
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

            // UseItems namespace: 0x580 + (zeroBasedUseItemIndex + 1).
            // Minimum valid UseItem protocol index is therefore 0x581.
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

            // Normal Items.xml namespace uses the table index directly.
            if (protocolTableIndex < ServerMain.Items.Count &&
                !(ServerMain.Items[protocolTableIndex] is UseItemTable.UseItem))
            {
                catalogIndex = protocolTableIndex;
                itemData = ServerMain.Items[catalogIndex];
                return true;
            }

            return false;
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
