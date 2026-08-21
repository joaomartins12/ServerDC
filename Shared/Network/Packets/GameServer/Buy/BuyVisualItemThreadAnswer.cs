using System.IO;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// BuyVisualItemAck (1204): 9 x 32-bit fields = 36-byte payload.
    /// DriftCity's packet handler consumes 0x26 (38) bytes including the 2-byte
    /// packet id. The TCP framing layer adds another 2-byte length prefix on wire.
    /// </summary>
    public class BuyVisualItemThreadAnswer : OutPacket
    {
        public int Type;
        public uint TableIndex;
        public uint CarId;
        public int InventoryId;
        public int Period;
        public int Mito;
        public int Hancoin;
        public int BonusMito;
        public int Mileage;

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.BuyVisualItemThreadAck);
        }

        public override int ExpectedSize() => 38;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.Write(Type);
                bs.Write(TableIndex);
                bs.Write(CarId);
                bs.Write(InventoryId);
                bs.Write(Period);
                bs.Write(Mito);
                bs.Write(Hancoin);
                bs.Write(BonusMito);
                bs.Write(Mileage);
                return ms.ToArray();
            }
        }
    }
}
