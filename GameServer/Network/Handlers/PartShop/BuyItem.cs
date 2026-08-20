using System;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class BuyItem
    {
        [Packet(Packets.CmdBuyItem)]
        public static void Handle(Packet packet)
        {
            var buyItemPacket = new BuyItemPacket(packet);

            if (buyItemPacket.TableIndex >= ServerMain.Items.Count)
            {
                packet.Sender.SendDebugError($"Item {buyItemPacket.TableIndex} out of range!");
#if !DEBUG
                packet.Sender.KillConnection("Invalid shop item");
#endif
                return;
            }

            var itemData = ServerMain.Items[buyItemPacket.TableIndex];
#if DEBUG
            Log.Debug($"{itemData.Id} - {itemData.Name} - {buyItemPacket.TableIndex}");
#endif
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

            var inventoryItem = character.GiveItem(GameServer.Instance.Database.Connection,
                buyItemPacket.TableIndex, buyItemPacket.Quantity);
            if (inventoryItem == null)
            {
                packet.Sender.SendDebugError("Giving item failed");
                return;
            }

            // Important: shop parts are deterministic base items. Do not manufacture a Random
            // value here. Items.xml BasePointVariable belongs to the dropped-item generation
            // flow; a normal Part Shop purchase must retain the instance values produced by
            // GiveItem (normally Random=0, UpgradePoint=0) so equipping cannot reinterpret it.
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
    }
}
