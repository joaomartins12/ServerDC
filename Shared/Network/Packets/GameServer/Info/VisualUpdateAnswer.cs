using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// Cmd_VisualUpdate / packet 1061, handler 0x529FD0 in Drift City v0.77a.
    ///
    /// The handler returns 0x3D, so the complete packet beginning with its id is
    /// exactly 61 bytes. Its accessed offsets prove the packed layout below:
    ///   PacketId       2
    ///   Serial         2   (+0x02)
    ///   Age            2   (+0x04)
    ///   CarId          4   (+0x06)
    ///   VisualState    1   (+0x0A)
    ///   XiVisualItem  50   (+0x0B)
    /// Total           61
    ///
    /// The previous implementation incorrectly serialized XiStrCarInfo after
    /// CarId. That happened to be close in size but put completely unrelated
    /// garage fields where the client expects the equipped XiVisualItem snapshot.
    /// </summary>
    public class VisualUpdateAnswer : OutPacket
    {
        public ushort Serial;
        public ushort Age;
        public uint CarId;

        // Copied by the client into the local vehicle visual-state byte. Retail
        // sends the normal active state here; zero is the safe/default state.
        public byte VisualState;

        public XiVisualItem VisualItem = new XiVisualItem();

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.VisualUpdateAck);
        }

        public override int ExpectedSize() => 61;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.Write(Serial);
                bs.Write(Age);
                bs.Write(CarId);
                bs.Write(VisualState);
                bs.Write(VisualItem ?? new XiVisualItem());
                return ms.ToArray();
            }
        }
    }
}
