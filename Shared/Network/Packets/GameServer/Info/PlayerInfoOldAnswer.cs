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
        public ushort PacketId = PlayerInfoLivePacketId;

        public override Packet CreatePacket()
        {
            return base.CreatePacket(PacketId);
        }

        public override int ExpectedSize()
        {
            var extraInfos = PlayerInfos ?? new XiPlayerInfo[0];
            return (216 * (extraInfos.Length + 1)) + 6;
        }

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                var extraInfos = PlayerInfos ?? new XiPlayerInfo[0];

                // Both Cmd_PlayerInfoOld (802) and Cmd_PlayerInfoRes (809) use a
                // DWORD count followed by one 0xD8-byte XiPlayerInfo per player.
                // Do not manufacture two entries with the same serial to force a
                // render transition: the retail handler treats them as two player
                // records, not as old/new visual states.
                bs.Write(extraInfos.Length + 1);
                bs.Write(PlayerInfo ?? new XiPlayerInfo());
                foreach (var playerInfo in extraInfos)
                    bs.Write(playerInfo ?? new XiPlayerInfo());

                return ms.ToArray();
            }
        }
    }
}
