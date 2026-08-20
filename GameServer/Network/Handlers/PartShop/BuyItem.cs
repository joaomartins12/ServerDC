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
            // For UseItems we immediately persist the protocol TableIndex afterwards, because that
            // is the value XiStrMyItem must send back to the client on ItemList/ItemMod packets.
            var inventoryItem = character.GiveItem(
                GameServer.Instance.Database.Connection,
                catalogIndex,
                buyItemPacket.Quantity);
            if (inventoryItem == null)
            {
                packet.Sender.SendDebugError("Giving item failed");
                return;
            }

            if (isUseItem && inventoryItem.TableIndex != (uint)protocolTableIndex)
            {
                var oldIndex = inventoryItem.TableIndex;
                inventoryItem.TableIndex = checked((uint)protocolTableIndex);
                ItemModel.Update(GameServer.Instance.Database.Connection, inventoryItem);

                Log.Info(
                    "UseItem protocol index persisted: DbId={0} InvenIdx={1} CatalogIndex={2} OldTableIndex={3} ProtocolTableIndex={4} ItemId={5} Name={6}",
                    inventoryItem.DbId,
                    inventoryItem.InventoryIndex,
                    catalogIndex,
                    oldIndex,
                    inventoryItem.TableIndex,
                    itemData.Id,
                    itemData.Name);
            }

            Log.Debug(
                "PartShop item instance: DbId={0} InvenIdx={1} TableIndex={2} Random={3} Upgrade={4} UpgradePoint={5}",
                inventoryItem.DbId,
                inventoryItem.InventoryIndex,
                inventoryItem.TableIndex,
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
