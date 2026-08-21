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

        private static uint GetClientColor(Vehicle vehicle)
        {
            if (vehicle == null) return 0;
            return vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor;
        }

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
                    var clientColor = GetClientColor(car);

                    bs.Write(car.CarId);
                    bs.Write(car.CarType);

                    // v0.77a seeds its garage/world render cache from the first colour
                    // DWORD in XiStrCarInfo. Keep the persistent BaseColor untouched in
                    // the model/DB, but put the current effective paint on the wire here.
                    bs.Write(clientColor);

                    bs.Write(car.Grade);
                    bs.Write(car.SlotType);
                    bs.Write(car.AuctionCnt);
                    bs.Write(car.Mitron);
                    bs.Write(car.Kmh);
                    bs.Write(clientColor);
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
