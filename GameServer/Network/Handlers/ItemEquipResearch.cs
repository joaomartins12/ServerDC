using System;
using System.Linq;
using Shared.Models;
using Shared.Network;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    /// <summary>
    /// Item equip flow reconstructed from client captures.
    /// CmdEquipItem payload is three UInt32 values:
    /// InventoryIndex, TargetSlot, CarId.
    /// Observed target slots start at 100 (100, 101, 106, ...).
    /// </summary>
    public static class ItemEquipResearch
    {
        [Packet(Packets.CmdEquipItem)]
        public static void Equip(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null || character.ActiveCar == null)
            {
                Log.Warning("CmdEquipItem ignored: no active character/car.");
                return;
            }

            if (packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position < 12)
            {
                TraceRemaining(packet, "CmdEquipItem", Packets.CmdEquipItem);
                return;
            }

            var inventoryIndex = packet.Reader.ReadUInt32();
            var targetSlot = packet.Reader.ReadUInt32();
            var carId = packet.Reader.ReadUInt32();

            Log.Info(
                "CmdEquipItem: CID={0} InvenIdx={1} TargetSlot={2} CarId={3} ActiveCar={4}",
                character.Id, inventoryIndex, targetSlot, carId, character.ActiveCar.CarId);

            if (carId != character.ActiveCar.CarId)
            {
                Log.Warning("CmdEquipItem rejected: requested CarId={0}, active CarId={1}.", carId, character.ActiveCar.CarId);
                return;
            }

            var item = character.InventoryItems.FirstOrDefault(x => x.InventoryIndex == inventoryIndex);
            if (item == null)
            {
                Log.Warning("CmdEquipItem rejected: inventory index {0} not found.", inventoryIndex);
                return;
            }

            if (targetSlot > ushort.MaxValue)
            {
                Log.Warning("CmdEquipItem rejected: target slot {0} out of range.", targetSlot);
                return;
            }

            using (var connection = GameServer.Instance.Database.Connection)
            {
                // Only one item can occupy a given equipment slot on the same car.
                var previous = character.InventoryItems.FirstOrDefault(x =>
                    x != item && x.CarId == carId && x.State == 1 && x.Slot == (ushort)targetSlot);

                if (previous != null)
                {
                    previous.State = 0;
                    previous.Slot = 0;
                    previous.CarId = carId;
                    ItemModel.Update(connection, previous);
                    character.AddItemMod(previous, true);
                }

                item.LastCarId = item.CarId;
                item.CarId = carId;
                item.State = 1; // 0=inventory, 1=equipped (confirmed by XiStrMyItem notes/captures)
                item.Slot = (ushort)targetSlot;
                item.Belonging = 1;
                ItemModel.Update(connection, item);
                character.AddItemMod(item, true);
            }

            character.FlushItemModBuffer(packet.Sender);

            Log.Info(
                "Item equipped: DbId={0} InvenIdx={1} TableIndex={2} CarId={3} Slot={4} State={5}",
                item.DbId, item.InventoryIndex, item.TableIndex, item.CarId, item.Slot, item.State);

            // The client asks for CmdCheckStat after inventory changes in observed sessions;
            // send it immediately as well so the panel updates without waiting for another UI refresh.
            CheckStat.Handle(packet);
        }

        [Packet(Packets.CmdUnEquipItem)]
        public static void UnEquip(Packet packet)
        {
            // We do not yet have a captured 411 payload. Keep this path capture-only until one is observed.
            TraceRemaining(packet, "CmdUnEquipItem", Packets.CmdUnEquipItem);
        }

        private static void TraceRemaining(Packet packet, string name, ushort packetId)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            var stream = packet.Reader.BaseStream;
            var remaining = Math.Max(0L, stream.Length - stream.Position);
            var bytes = remaining > 0 ? packet.Reader.ReadBytes(checked((int)remaining)) : new byte[0];

            Log.Warning(
                "INVENTORY RESEARCH {0} ({1},0x{1:X}): CID={2} ActiveCarDbId={3} PayloadBytes={4}",
                name,
                packetId,
                character == null ? 0UL : character.Id,
                character == null || character.ActiveCar == null ? 0U : character.ActiveCar.CarId,
                bytes.Length);

            if (bytes.Length > 0)
                Log.Debug("{0} payload HEX:\n{1}", name, BinaryWriterExt.HexDump(bytes));
        }
    }
}
