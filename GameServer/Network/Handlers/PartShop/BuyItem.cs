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

            // The client uses InventoryItem.Random when presenting variable part attributes.
            // A zero value lets that presentation be regenerated between sessions. Assign the
            // seed once, persist it, and never mutate it again so the acquired part is stable.
            if (IsVehiclePart(itemData.Category) && inventoryItem.Random == 0)
            {
                inventoryItem.Random = CreateStablePartSeed(inventoryItem);
                ItemModel.Update(GameServer.Instance.Database.Connection, inventoryItem);
                Log.Info(
                    "Part instance stabilized: DbId={0} InvenIdx={1} TableIndex={2} Item={3} Category={4} Random={5} BasePoints={6} UpgradePoint={7}",
                    inventoryItem.DbId,
                    inventoryItem.InventoryIndex,
                    inventoryItem.TableIndex,
                    itemData.Name ?? "UNKNOWN",
                    itemData.Category ?? "UNKNOWN",
                    inventoryItem.Random,
                    GetBasePoints(itemData),
                    inventoryItem.UpgradePoint);
            }

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

        private static bool IsVehiclePart(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return false;
            var value = category.Trim().ToLowerInvariant();
            return value == "speed" || value == "accel" || value == "acceleration" ||
                   value == "crash" || value == "durability" || value == "boost" || value == "booster";
        }

        private static int CreateStablePartSeed(InventoryItem item)
        {
            unchecked
            {
                var seed = (item.DbId * 397) ^ (item.TableIndex * 7919) ^ (int)item.InventoryIndex ^ 0x35A4E21;
                seed &= int.MaxValue;
                return seed == 0 ? 1 : seed;
            }
        }

        private static string GetBasePoints(Shared.Objects.GameDatas.BasicItem item)
        {
            var part = item as Shared.Objects.GameDatas.ItemTable.Item;
            return part == null ? "n/a" : (part.BasePoints ?? "n/a");
        }
    }
}
