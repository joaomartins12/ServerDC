using System.Collections.Generic;
using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// VisualItemListAck (1201). Header is ListUpdate + ItemNum followed by
    /// 120-byte XiStrMyVSItem records. The empty response still carries one null
    /// record, matching the v0.77 capture (132 wire bytes including packet header).
    /// </summary>
    public class VisualItemListAnswer : OutPacket
    {
        public int ListUpdate = 262144;
        public List<InventoryVisualItem> Items = new List<InventoryVisualItem>();

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.VisualItemListAck);
        }

        public override int ExpectedSize()
        {
            return 12 + (120 * (Items.Count == 0 ? 1 : Items.Count));
        }

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.Write(ListUpdate);
                bs.Write(Items.Count);

                if (Items.Count == 0)
                {
                    bs.Write(new byte[120]);
                }
                else
                {
                    foreach (var item in Items)
                        bs.Write(item);
                }

                return ms.ToArray();
            }
        }
    }
}
