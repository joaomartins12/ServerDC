using System;
using System.Globalization;
using System.Linq;
using GameServer.Util;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class ItemList
    {
        private const int UseItemProtocolBase = 0x580;

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
                var firstUseItemIndex = FindFirstUseItemCatalogIndex();

                character.InventoryItems.Clear();
                character.InventoryItems.AddRange(items);

                Log.Info("Inventory load: CID={0} Name={1} Items={2}", character.Id, character.Name, items.Count);
                foreach (var inventoryItem in items)
                {
                    string itemId = "UNKNOWN";
                    string itemName = "UNKNOWN";
                    string category = "UNKNOWN";
                    string protocolKind = "unresolved";
                    BasicItem definition = null;
                    Vehicle linkedVehicle = null;

                    // A car key is identified from the vehicle instance it belongs to. The original
                    // UseItems.xml maxstack field matches CarType. Earlier builds encoded keys using
                    // their physical XML ordinal; repair those rows to the pc_XXXX key-number form.
                    BasicItem expectedKey;
                    int expectedKeyCatalogIndex;
                    int expectedKeyUseItemIndex;
                    int expectedKeyProtocolIndex;
                    int oldXmlProtocolIndex;
                    if (TryResolveVehicleKeyForInventory(
                        character,
                        inventoryItem,
                        firstUseItemIndex,
                        out linkedVehicle,
                        out expectedKey,
                        out expectedKeyCatalogIndex,
                        out expectedKeyUseItemIndex,
                        out expectedKeyProtocolIndex,
                        out oldXmlProtocolIndex))
                    {
                        if (inventoryItem.TableIndex == oldXmlProtocolIndex &&
                            expectedKeyProtocolIndex != oldXmlProtocolIndex)
                        {
                            var oldProtocol = inventoryItem.TableIndex;
                            inventoryItem.TableIndex = expectedKeyProtocolIndex;
                            ItemModel.Update(connection, inventoryItem);
                            Log.Info(
                                "Vehicle key protocol migration: DbId={0} CarId={1} ItemId={2} Name='{3}' XmlUseItemIndex={4} OldProtocol={5} NewProtocol={6}",
                                inventoryItem.DbId,
                                inventoryItem.CarId,
                                expectedKey.Id,
                                expectedKey.Name,
                                expectedKeyUseItemIndex,
                                oldProtocol,
                                expectedKeyProtocolIndex);
                        }

                        if (inventoryItem.TableIndex == expectedKeyProtocolIndex)
                        {
                            definition = expectedKey;
                            protocolKind = "vehicle-key/item-id";
                        }
                    }

                    // Normal UseItems use the namespace confirmed from the real Mittron Fuel (5L)
                    // purchase: 0x580 + (zero-based UseItems.xml index + 1).
                    if (definition == null && firstUseItemIndex >= 0 && inventoryItem.TableIndex > UseItemProtocolBase)
                    {
                        var useItemIndex = inventoryItem.TableIndex - UseItemProtocolBase - 1;
                        var useItemCatalogIndex = firstUseItemIndex + useItemIndex;
                        if (useItemIndex >= 0 &&
                            useItemCatalogIndex >= firstUseItemIndex &&
                            useItemCatalogIndex < ServerMain.Items.Count &&
                            ServerMain.Items[useItemCatalogIndex] is UseItemTable.UseItem)
                        {
                            definition = ServerMain.Items[useItemCatalogIndex];
                            protocolKind = "useitem/xml-order";
                        }
                    }

                    // Items.xml uses its normal direct table index.
                    if (definition == null && ServerMain.Items != null &&
                        inventoryItem.TableIndex >= 0 && inventoryItem.TableIndex < ServerMain.Items.Count &&
                        !(ServerMain.Items[inventoryItem.TableIndex] is UseItemTable.UseItem))
                    {
                        definition = ServerMain.Items[inventoryItem.TableIndex];
                        protocolKind = "item/direct";
                    }

                    if (definition != null)
                    {
                        itemId = definition.Id ?? "UNKNOWN";
                        itemName = definition.Name ?? "UNKNOWN";
                        category = definition.Category ?? "UNKNOWN";
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
                        "Inventory item: DbId={0} InvenIdx={1} TableIndex={2} Protocol={3} ItemId={4} Name={5} Category={6} Stack={7} CarId={8} State={9} Slot={10} Upgrade={11} UpgradePoint={12} Durability={13} Random={14} LastCarId={15}",
                        inventoryItem.DbId,
                        inventoryItem.InventoryIndex,
                        inventoryItem.TableIndex,
                        protocolKind,
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

                    // CmdInventoryRequest (1156) has no per-item payload; it is a general refresh.
                    // For car keys, log every piece of vehicle information currently available so
                    // we can identify the real client tooltip/detail flow without inventing a packet.
                    if (definition != null && IsVehicleKey(definition) && linkedVehicle != null)
                    {
                        var stats = VehicleStatResolver.Resolve(linkedVehicle);
                        Log.Info(
                            "Vehicle key info: DbId={0} ItemId={1} Name='{2}' ProtocolTableIndex={3} CarId={4} CarType={5} Grade=V{6} Km={7:F2} Mitron={8:F2} Capacity={9:F3} Efficiency={10:F3} Stats[S={11},C={12},A={13},B={14}] Source={15}",
                            inventoryItem.DbId,
                            definition.Id,
                            definition.Name,
                            inventoryItem.TableIndex,
                            linkedVehicle.CarId,
                            linkedVehicle.CarType,
                            linkedVehicle.Grade,
                            linkedVehicle.Kmh,
                            linkedVehicle.Mitron,
                            stats.MitronCapacity,
                            stats.MitronEfficiency,
                            stats.Speed,
                            stats.Crash,
                            stats.Accel,
                            stats.Boost,
                            stats.Source);
                    }
                }

                var ack = new ItemListAnswer { InventoryItems = items.ToArray() };
                packet.Sender.Send(ack.CreatePacket());
            }
        }

        private static bool TryResolveVehicleKeyForInventory(
            Character character,
            InventoryItem inventoryItem,
            int firstUseItemIndex,
            out Vehicle linkedVehicle,
            out BasicItem keyDefinition,
            out int keyCatalogIndex,
            out int keyUseItemIndex,
            out int keyProtocolIndex,
            out int oldXmlProtocolIndex)
        {
            linkedVehicle = null;
            keyDefinition = null;
            keyCatalogIndex = -1;
            keyUseItemIndex = -1;
            keyProtocolIndex = -1;
            oldXmlProtocolIndex = -1;

            if (character == null || inventoryItem == null || inventoryItem.CarId == 0 ||
                firstUseItemIndex < 0 || ServerMain.Items == null || character.GarageVehicles == null)
                return false;

            linkedVehicle = character.GarageVehicles.FirstOrDefault(v =>
                v != null && v.CarId == inventoryItem.CarId);
            if (linkedVehicle == null)
                return false;

            for (var i = firstUseItemIndex; i < ServerMain.Items.Count; i++)
            {
                var useItem = ServerMain.Items[i] as UseItemTable.UseItem;
                if (useItem == null || !IsVehicleKey(useItem))
                    continue;

                uint mappedCarType;
                if (!uint.TryParse(useItem.MaxStack, NumberStyles.Integer, CultureInfo.InvariantCulture, out mappedCarType) ||
                    mappedCarType != linkedVehicle.CarType)
                    continue;

                int keyNumber;
                if (!TryGetVehicleKeyNumber(useItem.Id, out keyNumber))
                    return false;

                keyDefinition = useItem;
                keyCatalogIndex = i;
                keyUseItemIndex = i - firstUseItemIndex;
                oldXmlProtocolIndex = checked(UseItemProtocolBase + keyUseItemIndex + 1);
                keyProtocolIndex = checked(UseItemProtocolBase + keyNumber + 1);
                return true;
            }

            return false;
        }

        private static bool TryGetVehicleKeyNumber(string itemId, out int keyNumber)
        {
            keyNumber = -1;
            if (string.IsNullOrWhiteSpace(itemId) ||
                !itemId.StartsWith("pc_", StringComparison.OrdinalIgnoreCase))
                return false;

            var start = 3;
            var end = start;
            while (end < itemId.Length && char.IsDigit(itemId[end]))
                end++;

            if (end == start)
                return false;

            return int.TryParse(
                itemId.Substring(start, end - start),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out keyNumber);
        }

        private static int FindFirstUseItemCatalogIndex()
        {
            if (ServerMain.Items == null)
                return -1;

            for (var i = 0; i < ServerMain.Items.Count; i++)
            {
                if (ServerMain.Items[i] is UseItemTable.UseItem)
                    return i;
            }

            return -1;
        }

        private static bool IsVehicleKey(BasicItem item)
        {
            if (item == null) return false;
            if (!string.Equals((item.Category ?? string.Empty).Trim(), "car", StringComparison.OrdinalIgnoreCase))
                return false;
            return (item.Name ?? string.Empty).Trim().EndsWith("key", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVehiclePart(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return false;
            var value = category.Trim().ToLowerInvariant();
            return value == "speed" || value == "accel" || value == "acceleration" ||
                   value == "crash" || value == "durability" || value == "boost" || value == "booster";
        }

        private static int CreateLegacyArtificialSeed(InventoryItem item)
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
