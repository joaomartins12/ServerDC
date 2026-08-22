using System;
using System.IO;
using Shared.Objects;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// sub_53C7A0
    /// </summary>
    public class LoadCharThreadAnswer : OutPacket
    {
        public uint ServerId;
        public uint ServerStartTime;
        public Character Character = new Character();
        public Vehicle[] Vehicles = new Vehicle[0];
        public int CurrentCarId;

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.LoadCharThreadAck);
        }

        public override int ExpectedSize() => (50 * Vehicles.Length - 1) + 385;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.Write(ServerId);
                bs.Write(ServerStartTime);
                Character.Serialize(bs);
                bs.Write(Vehicles.Length);
                foreach (var vehicle in Vehicles)
                {
                    var car = vehicle ?? new Vehicle();
                    var customColor = car.Color != 0 ? car.Color : car.BaseColor;

                    bs.Write(car.CarId);
                    bs.Write(car.CarType);

                    // Retail XiStrCarInfo keeps the vehicle's original/base colour and
                    // the custom paint in two distinct DWORDs. Feeding the RGB paint into
                    // BaseColor made the world body use an invalid base state while visual
                    // parts consumed Color, producing mixed white/custom-colour cars.
                    bs.Write(car.BaseColor);

                    bs.Write(car.Grade);
                    bs.Write(car.SlotType);
                    bs.Write(car.AuctionCnt);
                    bs.Write(car.Mitron);
                    bs.Write(car.Kmh);
                    bs.Write(customColor);
                    bs.Write(car.Color2);
                    bs.Write(car.MitronCapacity);
                    bs.Write(car.MitronEfficiency);
                    bs.Write(car.AuctionOn);
                    bs.Write(car.SBBOn);
                }
                return ms.ToArray();
            }
        }
    }
}
