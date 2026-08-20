using System;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class ItemList
    {
        [Packet(Packets.CmdItemList)]
        public static void Handle(Packet packet)
        {
            var character = packet.Sender.User.ActiveCharacter;
            if (character == null)
            {
                Log.Warning("ItemList requested without an active character. Endpoint={0}", packet.Sender.EndPoint);
                return;
            }

            using (var connection = GameServer.Instance.Database.Connection)
            {
                var items = ItemModel.RetrieveAll(connection, character.Id);

                character.InventoryItems.Clear();
                character.InventoryItems.AddRange(items);

                Log.Info("Inventory load: CID={0} Name={1} Items={2}", character.Id, character.Name, items.Count);
                foreach (var inventoryItem in items)
                {
                    string itemId = "UNKNOWN";
                    string itemName = "UNKNOWN";
                    string category = "UNKNOWN";
                    Shared.Objects.GameDatas.BasicItem definition = null;

                    if (ServerMain.Items != null && inventoryItem.TableIndex >= 0 && inventoryItem.TableIndex < ServerMain.Items.Count)
                    {
                        definition = ServerMain.Items[inventoryItem.TableIndex];
                        if (definition != null)
                        {
                            itemId = definition.Id ?? "UNKNOWN";
                            itemName = definition.Name ?? "UNKNOWN";
                            category = definition.Category ?? "UNKNOWN";
                        }
                    }

                    // Older inventory rows were created with Random=0. Stabilize vehicle parts once
                    // and persist the seed so the client sees the exact same instance every login.
                    if (definition != null && IsVehiclePart(category) && inventoryItem.Random == 0)
                    {
                        inventoryItem.Random = CreateStablePartSeed(inventoryItem);
                        ItemModel.Update(connection, inventoryItem);
                        Log.Info(
                            "Legacy part instance stabilized: DbId={0} InvenIdx={1} TableIndex={2} Name={3} Category={4} Random={5}",
                            inventoryItem.DbId,
                            inventoryItem.InventoryIndex,
                            inventoryItem.TableIndex,
                            itemName,
                            category,
                            inventoryItem.Random);
                    }

                    Log.Debug(
                        "Inventory item: DbId={0} InvenIdx={1} TableIndex={2} ItemId={3} Name={4} Category={5} Stack={6} CarId={7} State={8} Slot={9} Upgrade={10} UpgradePoint={11} Durability={12} Random={13}",
                        inventoryItem.DbId,
                        inventoryItem.InventoryIndex,
                        inventoryItem.TableIndex,
                        itemId,
                        itemName,
                        category,
                        inventoryItem.StackNum,
                        inventoryItem.CarId,
                        inventoryItem.State,
                        inventoryItem.Slot,
                        inventoryItem.Upgrade,
                        inventoryItem.UpgradePoint,
                        inventoryItem.Durability,
                        inventoryItem.Random);
                }

                var ack = new ItemListAnswer { InventoryItems = items.ToArray() };
                packet.Sender.Send(ack.CreatePacket());
            }
        }

        private static bool IsVehiclePart(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return false;
            var value = category.Trim().ToLowerInvariant();
            return value == "speed" || value == "accel" || value == "acceleration" ||
                   value == "crash" || value == "durability" || value == "boost" || value == "booster";
        }

        private static int CreateStablePartSeed(Shared.Objects.InventoryItem item)
        {
            unchecked
            {
                var seed = (item.DbId * 397) ^ (item.TableIndex * 7919) ^ (int)item.InventoryIndex ^ 0x35A4E21;
                seed &= int.MaxValue;
                return seed == 0 ? 1 : seed;
            }
        }
    }
}
