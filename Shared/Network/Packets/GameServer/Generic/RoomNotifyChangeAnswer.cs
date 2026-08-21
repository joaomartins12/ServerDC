using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// BS_PktRoomNotifyChange / packet 467.
    /// Retail v0.77a world snapshot used to rebuild an already known vehicle by serial.
    /// Body: Serial(2) + Age(2) + XiCarAttr(8) + XiPlayerInfo(216) + tail(12) = 240 bytes.
    /// </summary>
    public class RoomNotifyChangeAnswer : OutPacket
    {
        public ushort Age;
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
                bs.Write(Age);
                bs.Write(CarAttr.___u0.llval);
                bs.Write(PlayerInfo);
                bs.Write(new byte[12]);
                return ms.ToArray();
            }
        }
    }
}
