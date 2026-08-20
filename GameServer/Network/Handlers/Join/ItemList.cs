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
        [Packet((ushort)1156)] // CmdInventoryRequest observed after equip/unequip and purchases.
        public static void Handle(Packet packet)
        {
            var character = packet.Sender.User.ActiveCharacter;
            if (character == null)
            {
                Log.Warning("ItemList requested without an active character. Endpoint={0}", packet.Sender.EndPoint);
                return;
            }

            if (packet.Id == 1156)
                Log.Debug("CmdInventoryRequest refresh: CID={0} Name={1}", character.Id, character.Name);

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

                    // Earlier server builds generated a deterministic but protocol-invalid Random
                    // value for every normal shop part. Only undo values that exactly match that
                    // old formula. Legitimate Random values from dropped/generated items are left
                    // untouched because BasePointVariable is part of the drop-item generation flow.
                    if (definition != null && IsVehiclePart(category) && inventoryItem.Random != 0)
                    {
                        var badSeed = CreateLegacyArtificialSeed(inventoryItem);
                        if (inventoryItem.Random == badSeed)
                        {
                            Log.Info(
                                "Reverting artificial part Random: DbId={0} InvenIdx={1} TableIndex={2} Name={3} OldRandom={4}",
                                inventoryItem.DbId,
                                inventoryItem.InventoryIndex,
                                inventoryItem.TableIndex,
                                itemName,
                                inventoryItem.Random);
                            inventoryItem.Random = 0;
                            ItemModel.Update(connection, inventoryItem);
                        }
                    }

                    Log.Debug(
                        "Inventory item: DbId={0} InvenIdx={1} TableIndex={2} ItemId={3} Name={4} Category={5} Stack={6} CarId={7} State={8} Slot={9} Upgrade={10} UpgradePoint={11} Durability={12} Random={13} LastCarId={14}",
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
                        inventoryItem.Random,
                        inventoryItem.LastCarId);
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

        private static int CreateLegacyArtificialSeed(Shared.Objects.InventoryItem item)
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
