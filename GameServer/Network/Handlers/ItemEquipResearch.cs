using System;
using Shared.Network;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    /// <summary>
    /// Temporary protocol-research handlers for the client item equip flow.
    /// We deliberately do not mutate inventory or manufacture an ACK until the
    /// exact client payload/expected response has been established from captures.
    /// </summary>
    public static class ItemEquipResearch
    {
        [Packet(Packets.CmdEquipItem)]
        public static void Equip(Packet packet)
        {
            Trace(packet, "CmdEquipItem", Packets.CmdEquipItem);
        }

        [Packet(Packets.CmdUnEquipItem)]
        public static void UnEquip(Packet packet)
        {
            Trace(packet, "CmdUnEquipItem", Packets.CmdUnEquipItem);
        }

        private static void Trace(Packet packet, string name, ushort packetId)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            var stream = packet.Reader.BaseStream;
            var remaining = Math.Max(0L, stream.Length - stream.Position);
            var bytes = remaining > 0 ? packet.Reader.ReadBytes(checked((int)remaining)) : new byte[0];

            Log.Warning(
                "INVENTORY RESEARCH {0} ({1},0x{1:X}): CID={2} Character={3} ActiveCarDbId={4} ActiveCarType={5} ActiveGrade={6} PayloadBytes={7}",
                name,
                packetId,
                character == null ? 0UL : character.Id,
                character == null ? "<none>" : character.Name,
                character == null || character.ActiveCar == null ? 0U : character.ActiveCar.CarId,
                character == null || character.ActiveCar == null ? 0U : character.ActiveCar.CarType,
                character == null || character.ActiveCar == null ? 0U : character.ActiveCar.Grade,
                bytes.Length);

            if (bytes.Length > 0)
                Log.Debug("{0} payload HEX:\n{1}", name, BinaryWriterExt.HexDump(bytes));

            if (character != null && character.InventoryItems != null)
            {
                foreach (var item in character.InventoryItems)
                {
                    Log.Debug(
                        "{0} inventory snapshot: DbId={1} InvenIdx={2} TableIndex={3} CarId={4} State={5} Slot={6} Stack={7} Upgrade={8} UpgradePoint={9}",
                        name,
                        item.DbId,
                        item.InventoryIndex,
                        item.TableIndex,
                        item.CarId,
                        item.State,
                        item.Slot,
                        item.StackNum,
                        item.Upgrade,
                        item.UpgradePoint);
                }
            }

            Log.Warning("{0}: capture-only handler; no inventory mutation/ACK sent until protocol layout is confirmed.", name);
        }
    }
}
