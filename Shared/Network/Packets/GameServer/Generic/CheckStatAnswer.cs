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

        // XiStrStatInfo in the original emulator documents exactly 32 missing bytes here.
        // Runtime probing confirmed that writing a ninth int at offset 0x70 changes the
        // client's EnchantBonus.Speed, so this section is 8 x 4 bytes, not 10 x 4 bytes.
        // Keep the names provisional until the current client mapping is fully understood.
        public int PerformanceUnknown1;
        public int PerformanceUnknown2;
        public int PerformanceUnknown3;
        public int PerformanceUnknown4;
        public int VehicleSpeed;
        public int VehicleDurability;
        public int VehicleAcceleration;
        public int VehicleBoost;

        // XiStrEnchantBonus begins at payload offset 0x70.
        public int Speed;
        public int Crash;
        public int Accel;
        public int Boost;
        public int AddSpeed;
        public float Drop;
        public float Exp;
        public float MitronCapacity;
        public float MitronEfficiency;

        // StatUpdate payload is 156 bytes (158 including packet id). The known stat info
        // and enchant structures account for 148 bytes, leaving these final 8 bytes.
        public int TrailingUnknown1;
        public int TrailingUnknown2;

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.StatUpdateAck);
        }

        public override int ExpectedSize() => 158;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
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

                bs.Write(Speed);
                bs.Write(Crash);
                bs.Write(Accel);
                bs.Write(Boost);
                bs.Write(AddSpeed);
                bs.Write(Drop);
                bs.Write(Exp);
                bs.Write(MitronCapacity);
                bs.Write(MitronEfficiency);

                bs.Write(TrailingUnknown1);
                bs.Write(TrailingUnknown2);

                return ms.ToArray();
            }
        }
    }
}
