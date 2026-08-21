using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// BS_PktRoomNotifyChange / packet 467.
    ///
    /// Drift City v0.77a's registered handler at 0x5402E0 returns 0xF0,
    /// therefore the complete packet beginning with the 2-byte packet id is
    /// exactly 240 bytes. The retail layout is:
    ///   PacketId       2 bytes
    ///   Serial         2 bytes
    ///   XiCarAttr      8 bytes
    ///   XiPlayerInfo 216 bytes
    ///   Unknown tail  12 bytes
    /// Total          240 bytes.
    ///
    /// There is NO separate Age word between Serial and XiCarAttr. Adding one
    /// shifts Sort/Body/Color and the entire XiPlayerInfo by two bytes; the
    /// remote client then rebuilds the wrong vehicle model before falling back.
    /// </summary>
    public class RoomNotifyChangeAnswer : OutPacket
    {
        public ushort Serial;

        public XiCarAttr CarAttr = new XiCarAttr();
        public XiPlayerInfo PlayerInfo = new XiPlayerInfo();

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.RoomNotifyChangeAck);
        }

        public override int ExpectedSize() => 240;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.Write(Serial);

                // XiCarAttr is an 8-byte union. Write exactly one representation.
                bs.Write(CarAttr.___u0.llval);

                bs.Write(PlayerInfo);

                // 2 id + 2 serial + 8 attr + 216 player + 12 tail = 240.
                bs.Write(new byte[12]);
                return ms.ToArray();
            }
        }
    }
}
