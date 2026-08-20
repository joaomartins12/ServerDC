using System;
using System.IO;
using Shared.Network.AreaServer;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// sub_529160
    /// PKTSIZE: 1177-byte body.
    /// </summary>
    public class GameCharInfoAnswer : OutPacket
    {
        public Character Character;
        public Vehicle Vehicle;
        public XiStrStatInfo StatisticInfo;
        public Crew Crew;
        public uint Serial;
        public int LocType = 2;
        public char ChId;
        public ushort LocId;

        public GameCharInfoAnswer()
        {
            Character = new Character();
            Vehicle = new Vehicle();
            StatisticInfo = new XiStrStatInfo();
            Crew = new Crew();
        }

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.GameCharInfoAck);
        }

        public override int ExpectedSize() => 1177;

        /// <summary>
        /// Layout after the main character/vehicle/stat/crew blocks:
        /// field_10 = 12 reserved bytes
        /// field_11 = session vehicle serial (uint) + channel id (ushort)
        /// field_12 = reserved int + location id (ushort)
        /// field_13 = location type (int)
        ///
        /// The previous implementation wrote zeroes into field_11/field_12 even though
        /// Serial, ChId and LocId were populated by the handler. That prevented the client
        /// from associating the profile vehicle with the player's live vehicle/session.
        /// </summary>
        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            {
                using (var bs = new BinaryWriterExt(ms))
                {
                    Character.Serialize(bs);
                    Vehicle.Serialize(bs);
                    StatisticInfo.Serialize(bs);

                    if (Crew == null)
                        bs.Write(new byte[664]);
                    else
                        Crew.Serialize(bs);

                    // field_10 - reserved
                    bs.Write(new byte[12]);

                    // field_11 - live player/session identity used by the profile preview.
                    bs.Write(Serial);
                    bs.Write((ushort)ChId);

                    // field_12 - keep the unknown int reserved, but preserve LocId.
                    bs.Write(0);
                    bs.Write(LocId);

                    // field_13
                    bs.Write(LocType);
                }

                return ms.ToArray();
            }
        }
    }
}
