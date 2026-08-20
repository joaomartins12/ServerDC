using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// BS_PktRoomNotifyChange / packet 467.
    ///
    /// Body layout used by the v0.77 client:
    ///   Serial      2 bytes
    ///   Age         2 bytes
    ///   XiCarAttr   8 bytes (union - serialize ONE representation only)
    ///   XiPlayerInfo 216 bytes
    ///   Unknown tail 12 bytes
    /// Total: 240 bytes.
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

                // XiCarAttr is an 8-byte UNION. The old implementation serialized
                // all three union views one after another, shifting XiPlayerInfo and
                // making the visual snapshot invalid for the client.
                bs.Write(CarAttr.___u0.llval);

                bs.Write(PlayerInfo);

                // Preserve the still-undocumented 12-byte tail so the packet body
                // remains exactly the 240 bytes expected by this client build.
                bs.Write(new byte[12]);
                return ms.ToArray();
            }
        }
    }
}
