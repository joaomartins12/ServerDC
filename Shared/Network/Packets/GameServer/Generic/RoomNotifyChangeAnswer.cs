using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// Cmd_RoomNotifyChange / packet 467, handler 0x5402E0 in Drift City v0.77a.
    ///
    /// The handler returns 0xF0, i.e. the packet beginning with its 2-byte id is
    /// exactly 240 bytes. Disassembly proves the following offsets:
    ///   +0x00 PacketId       2 bytes
    ///   +0x02 Serial         4 bytes (handler reads DWORD [pkt+2])
    ///   +0x06 Age            2 bytes
    ///   +0x08 XiCarAttr     16 bytes (handler passes &pkt[8] to 0x4C8BB0)
    ///   +0x18 XiPlayerInfo 216 bytes (handler passes &pkt[0x18])
    /// Total: 240 bytes. There is no trailing padding block.
    /// </summary>
    public class RoomNotifyChangeAnswer : OutPacket
    {
        public uint Serial;
        public ushort Age;
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
                var attr = CarAttr ?? new XiCarAttr();

                // BuildCarAttr historically populated only the first 8 bytes. The retail
                // structure has a second colour DWORD and a state DWORD as well.
                var activeCar = PlayerInfo?.Character?.ActiveCar;
                if (activeCar != null)
                    attr.Color2 = activeCar.Color2;
                if (attr.State == 0)
                    attr.State = 1;

                bs.Write(Serial);
                bs.Write(Age);
                bs.Write(attr);
                bs.Write(PlayerInfo ?? new XiPlayerInfo());
                return ms.ToArray();
            }
        }
    }
}
