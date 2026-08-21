using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// Cmd_VisualUpdate / packet 1061, handler 0x529FD0 in Drift City v0.77a.
    ///
    /// The handler returns 0x3D, so the complete packet beginning with its id is
    /// exactly 61 bytes. Disassembly shows the payload is:
    ///   PacketId       2
    ///   Serial         2   (+0x02)
    ///   Age            2   (+0x04)
    ///   CarId          4   (+0x06)
    ///   VisualState    1   (+0x0A)
    ///   XiStrCarInfo  50   (+0x0B)
    /// Total           61
    ///
    /// Proof from the retail handler: +0x0B is treated as CarID, +0x0F as
    /// CarType, +0x2B as Color and +0x2F as Color2, exactly matching
    /// XiStrCarInfo. The missing byte in the old emulator was VisualState.
    /// </summary>
    public class VisualUpdateAnswer : OutPacket
    {
        public ushort Serial;
        public ushort Age;
        public uint CarId;
        public byte VisualState;
        public XiStrCarInfo CarInfo = new XiStrCarInfo();

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
                (CarInfo ?? new XiStrCarInfo()).Serialize(bs);
                return ms.ToArray();
            }
        }
    }
}
