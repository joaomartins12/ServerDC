using System;
using System.Linq;
using GameServer.Network.Handlers.Dealership;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public static class ItemEquipResearch
    {
        [Packet(Packets.CmdEquipItem)]
        public static void Equip(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null || character.ActiveCar == null) return;

            if (packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position < 12)
            {
                TraceRemaining(packet, "CmdEquipItem", Packets.CmdEquipItem);
                return;
            }

            var inventoryIndex = packet.Reader.ReadUInt32();
            var targetSlot = packet.Reader.ReadUInt32();
            var carId = packet.Reader.ReadUInt32();
            if (carId != character.ActiveCar.CarId || targetSlot > ushort.MaxValue) return;

            var item = character.InventoryItems.FirstOrDefault(x => x.InventoryIndex == inventoryIndex);
            if (item == null) return;

            using (var connection = GameServer.Instance.Database.Connection)
            {
                var previous = character.InventoryItems.FirstOrDefault(x =>
                    x != item && x.CarId == carId && x.State == 1 && x.Slot == (ushort)targetSlot);

                if (previous != null)
                {
                    previous.LastCarId = 0;
                    previous.State = 0;
                    previous.Slot = 0;
                    previous.Belonging = 0;
                    ItemModel.Update(connection, previous);
                }

                item.LastCarId = 0;
                item.CarId = carId;
                item.State = 1;
                item.Slot = (ushort)targetSlot;
                item.Belonging = 1;
                ItemModel.Update(connection, item);
            }

            ResyncInventory(packet, character);
            Log.Info("Item equipped: InvenIdx={0} TableIndex={1} CarId={2} Slot={3} LastCarId={4}",
                item.InventoryIndex, item.TableIndex, item.CarId, item.Slot, item.LastCarId);
            CheckStat.Handle(packet);
        }

        [Packet(Packets.CmdUnEquipItem)]
        public static void UnEquip(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null) return;

            var remaining = (int)Math.Max(0L, packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position);
            var raw = remaining > 0 ? packet.Reader.ReadBytes(remaining) : new byte[0];

            Log.Info("CmdUnEquipItem: CID={0} PayloadBytes={1}", character.Id, raw.Length);
            if (raw.Length > 0)
                Log.Debug("CmdUnEquipItem payload HEX:\n{0}", BinaryWriterExt.HexDump(raw));

            uint a = 0, b = 0, c = 0;
            if (raw.Length >= 4) a = BitConverter.ToUInt32(raw, 0);
            if (raw.Length >= 8) b = BitConverter.ToUInt32(raw, 4);
            if (raw.Length >= 12) c = BitConverter.ToUInt32(raw, 8);

            var targetCarId = raw.Length >= 8 && b != 0
                ? b
                : character.ActiveCar != null ? character.ActiveCar.CarId : 0u;

            var equipped = character.InventoryItems
                .Where(x => x != null && x.State == 1 && (targetCarId == 0 || x.CarId == targetCarId))
                .ToList();

            InventoryItem item = equipped.FirstOrDefault(x => x.InventoryIndex == a);
            if (item == null) item = equipped.FirstOrDefault(x => x.Slot == a);
            if (item == null && raw.Length >= 12 && c != targetCarId)
                item = equipped.FirstOrDefault(x => x.InventoryIndex == c || x.Slot == c);
            if (item == null && equipped.Count == 1)
                item = equipped[0];

            if (item == null)
            {
                Log.Warning("CmdUnEquipItem: could not resolve equipped item from payload a={0} b={1} c={2} TargetCarId={3} Candidates={4}.",
                    a, b, c, targetCarId, equipped.Count);
                return;
            }

            using (var connection = GameServer.Instance.Database.Connection)
            {
                var previousCarId = item.CarId;
                item.LastCarId = 0;
                item.CarId = 0;
                item.State = 0;
                item.Slot = 0;
                item.Belonging = 0;
                ItemModel.Update(connection, item);
                character.AddItemMod(item, true);

                Log.Info("Item unequipped: InvenIdx={0} TableIndex={1} PreviousCarId={2} TargetCarId={3} -> CarId=0 LastCarId=0 State=0 Slot=0",
                    item.InventoryIndex, item.TableIndex, previousCarId, targetCarId);
            }

            ResyncInventory(packet, character);
            character.FlushItemModBuffer(packet.Sender);
            CheckStat.Handle(packet);

            var remainingOnCar = character.InventoryItems.Count(x =>
                x != null && x.State == 1 && x.CarId == targetCarId);
            Log.Info("CmdUnEquipItem complete: TargetCarId={0} RemainingEquipped={1}", targetCarId, remainingOnCar);

            // In the dealership sale flow the client does not send CmdSellCar after it has asked
            // the server to unequip the last part from a non-active vehicle. Finish the sale here
            // so the UI does not remain locked on the selected vehicle.
            var activeCarId = character.ActiveCar == null ? 0u : character.ActiveCar.CarId;
            if (remainingOnCar == 0 && targetCarId != 0 &&
                targetCarId != character.ActiveVehicleId && targetCarId != activeCarId)
            {
                SellCar.AutoCompleteAfterUnequip(packet, character, targetCarId);
            }
        }

        private static void ResyncInventory(Packet packet, Shared.Objects.Character character)
        {
            packet.Sender.Send(new ItemListAnswer
            {
                InventoryItems = character.InventoryItems.OrderBy(x => x.InventoryIndex).ToArray()
            }.CreatePacket());
        }

        private static void TraceRemaining(Packet packet, string name, ushort packetId)
        {
            var stream = packet.Reader.BaseStream;
            var remaining = Math.Max(0L, stream.Length - stream.Position);
            var bytes = remaining > 0 ? packet.Reader.ReadBytes(checked((int)remaining)) : new byte[0];
            Log.Warning("INVENTORY RESEARCH {0} ({1},0x{1:X}) PayloadBytes={2}", name, packetId, bytes.Length);
            if (bytes.Length > 0) Log.Debug("{0} payload HEX:\n{1}", name, BinaryWriterExt.HexDump(bytes));
        }
    }
}
