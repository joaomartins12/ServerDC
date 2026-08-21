using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    public class PlayerInfoOldAnswer : OutPacket
    {
        public const ushort PlayerInfoOldPacketId = 802;
        public const ushort PlayerInfoLivePacketId = 809;

        public XiPlayerInfo PlayerInfo = new XiPlayerInfo();
        public XiPlayerInfo[] PlayerInfos = new XiPlayerInfo[0];

        // Runtime visual refreshes use the retail PlayerInfoRes path (809 / 0x329).
        // Initial discovery/request-response callers can explicitly select 802.
        public ushort PacketId = PlayerInfoLivePacketId;
        
        public override Packet CreatePacket()
        {
            return base.CreatePacket(PacketId);
        }

        public override int ExpectedSize() => (216 * PlayerInfos.Length) + 222;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            {
                using (var bs = new BinaryWriterExt(ms))
                {
                    bs.Write(PlayerInfos.Length + 1);
                    bs.Write(PlayerInfo);
                    foreach (var playerInfo in PlayerInfos)
                    {
                        bs.Write(playerInfo);
                    }
                }
                return ms.ToArray();
            }
        }
    }
}