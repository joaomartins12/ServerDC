using System.Collections.Generic;
using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// VisualItemListAck (1201). Retail v0.77a uses an 8-byte payload header
    /// (ListUpdate + ItemNum), followed by exactly ItemNum 120-byte XiStrMyVSItem
    /// records. ListUpdate 0x40000 tells the client to clear its current visual
    /// inventory before inserting the supplied records.
    /// </summary>
    public class VisualItemListAnswer : OutPacket
    {
        public int ListUpdate = 0x40000;
        public List<InventoryVisualItem> Items = new List<InventoryVisualItem>();

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.VisualItemListAck);
        }

        public override int ExpectedSize()
        {
            return 12 + (120 * Items.Count);
        }

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.Write(ListUpdate);
                bs.Write(Items.Count);

                foreach (var item in Items)
                    bs.Write(item);

                return ms.ToArray();
            }
        }
    }
}
