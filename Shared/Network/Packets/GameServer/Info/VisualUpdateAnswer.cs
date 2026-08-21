using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
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
                var source = CarInfo ?? new XiStrCarInfo();
                var clientColor = source.Color != 0 ? source.Color : source.BaseColor;
                var carInfo = new XiStrCarInfo
                {
                    CarID = source.CarID,
                    CarType = source.CarType,
                    BaseColor = clientColor,
                    Grade = source.Grade,
                    SlotType = source.SlotType,
                    AuctionCnt = source.AuctionCnt,
                    Mitron = source.Mitron,
                    Kmh = source.Kmh,
                    Color = clientColor,
                    Color2 = source.Color2,
                    MitronCapacity = source.MitronCapacity,
                    MitronEfficiency = source.MitronEfficiency,
                    AuctionOn = source.AuctionOn,
                    SBBOn = source.SBBOn
                };

                bs.Write(Serial);
                bs.Write(Age);
                bs.Write(CarId);
                bs.Write(VisualState);
                carInfo.Serialize(bs);
                return ms.ToArray();
            }
        }
    }
}
