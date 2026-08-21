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
    /// exactly 240 bytes: PacketId(2) + Serial(2) + XiCarAttr(8) +
    /// XiPlayerInfo(216) + undocumented tail(12).
    ///
    /// Age is retained as an object-side compatibility field only. It is NOT a
    /// separate outer wire field; XiPlayerInfo already serializes its own Age.
    /// </summary>
    public class RoomNotifyChangeAnswer : OutPacket
    {
        public ushort Serial;
        public ushort Age; // compatibility only; intentionally not serialized
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
                bs.Write(CarAttr.___u0.llval);
                bs.Write(PlayerInfo);
                bs.Write(new byte[12]);
                return ms.ToArray();
            }
        }
    }
}
