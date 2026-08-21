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

        public override int ExpectedSize()
        {
            var extraInfos = PlayerInfos ?? new XiPlayerInfo[0];
            var count = extraInfos.Length + (PacketId == PlayerInfoLivePacketId ? 2 : 1);
            return (216 * count) + 6;
        }

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            {
                using (var bs = new BinaryWriterExt(ms))
                {
                    // Cmd_PlayerInfoRes (809) iterates every 0xD8-byte record and feeds it
                    // through the live player manager. The manager suppresses an identical
                    // snapshot when its cached XiVisualItem already matches, which is exactly
                    // what happened when a remote vehicle was created by AreaServer packet
                    // 541 after GameServer had already cached its visual state. For live 809
                    // updates send one blank visual state immediately followed by the real
                    // state in the SAME packet. This guarantees a visual delta/rebuild while
                    // preserving identity, serial, character and crew fields.
                    var extraInfos = PlayerInfos ?? new XiPlayerInfo[0];
                    var liveTransition = PacketId == PlayerInfoLivePacketId;
                    bs.Write(extraInfos.Length + (liveTransition ? 2 : 1));

                    if (liveTransition)
                    {
                        var source = PlayerInfo ?? new XiPlayerInfo();
                        var reset = new XiPlayerInfo(source.Serial, source.Character)
                        {
                            Age = source.Age,
                            UseTime = source.UseTime,
                            VisualItem = new XiVisualItem { PlateString = string.Empty }
                        };
                        bs.Write(reset);
                    }

                    bs.Write(PlayerInfo ?? new XiPlayerInfo());
                    foreach (var playerInfo in extraInfos)
                        bs.Write(playerInfo ?? new XiPlayerInfo());
                }

                return ms.ToArray();
            }
        }
    }
}
