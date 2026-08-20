using System.IO;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// sub_521CC00
    /// </summary>
    public class CheckStatAnswer : OutPacket
    {
        public int BasedSpeed;
        public int BasedDurability;
        public int BasedAcceleration;
        public int BasedBoost;

        public int EquipSpeed;
        public int EquipDurability;
        public int EquipAcceleration;
        public int EquipBoost;

        public int CharSpeed;
        public int CharDurability;
        public int CharAcceleration;
        public int CharBoost;

        public int ItemUseSpeed;
        public int ItemUseCrash;
        public int ItemUseAcceleration;
        public int ItemUseBoost;

        public int TotalSpeed;
        public int TotalDurability;
        public int TotalAcceleration;
        public int TotalBoost;

        // Reverse-engineered 40-byte vehicle performance section.
        // The original client research identified the middle four values as
        // Vehicle Speed / Durability / Acceleration / Boost. The client uses
        // these to derive the left-hand performance display (maximum speed,
        // time to reach, crash damage and boost time).
        public int PerformanceUnknown1;
        public int PerformanceUnknown2;
        public int PerformanceUnknown3;
        public int PerformanceUnknown4;
        public int VehicleSpeed;
        public int VehicleDurability;
        public int VehicleAcceleration;
        public int VehicleBoost;
        public int PerformanceUnknown9;
        public int PerformanceUnknown10;

        // EnChantBonus
        public int Speed;
        public int Crash;
        public int Accel;
        public int Boost;
        public int AddSpeed;
        public float Drop;
        public float Exp;
        public float MitronCapacity;
        public float MitronEfficiency;

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.StatUpdateAck);
        }

        public override int ExpectedSize() => 158;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            {
                using (var bs = new BinaryWriterExt(ms))
                {
                    bs.Write(BasedSpeed);
                    bs.Write(BasedDurability);
                    bs.Write(BasedAcceleration);
                    bs.Write(BasedBoost);

                    bs.Write(EquipSpeed);
                    bs.Write(EquipDurability);
                    bs.Write(EquipAcceleration);
                    bs.Write(EquipBoost);

                    bs.Write(CharSpeed);
                    bs.Write(CharDurability);
                    bs.Write(CharAcceleration);
                    bs.Write(CharBoost);

                    bs.Write(ItemUseSpeed);
                    bs.Write(ItemUseCrash);
                    bs.Write(ItemUseAcceleration);
                    bs.Write(ItemUseBoost);

                    bs.Write(TotalSpeed);
                    bs.Write(TotalDurability);
                    bs.Write(TotalAcceleration);
                    bs.Write(TotalBoost);

                    bs.Write(PerformanceUnknown1);
                    bs.Write(PerformanceUnknown2);
                    bs.Write(PerformanceUnknown3);
                    bs.Write(PerformanceUnknown4);
                    bs.Write(VehicleSpeed);
                    bs.Write(VehicleDurability);
                    bs.Write(VehicleAcceleration);
                    bs.Write(VehicleBoost);
                    bs.Write(PerformanceUnknown9);
                    bs.Write(PerformanceUnknown10);

                    // EnChantBonus
                    bs.Write(Speed);
                    bs.Write(Crash);
                    bs.Write(Accel);
                    bs.Write(Boost);
                    bs.Write(AddSpeed);
                    bs.Write(Drop);
                    bs.Write(Exp);
                    bs.Write(MitronCapacity);
                    bs.Write(MitronEfficiency);
                }
                return ms.ToArray();
            }
        }
    }
}
