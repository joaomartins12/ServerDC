using Shared.Network.GameServer;

namespace GameServer.Util
{
    /// <summary>
    /// Temporary runtime probe used to map StatUpdate fields against the current
    /// client's vehicle-performance UI. Fields 1-8 cover the 32-byte XiStrStatInfo
    /// tail. Runtime testing confirmed field 9 is Speed and field 10 is Crash/
    /// Durability in XiStrEnchantBonus; fields 11 and 12 are the corresponding
    /// Accel and Boost candidates. Field 0 disables probing.
    /// </summary>
    internal static class VehiclePerformanceProbe
    {
        private static readonly object Sync = new object();
        private static int _field;
        private static int _value;

        public static int Field
        {
            get { lock (Sync) return _field; }
        }

        public static int Value
        {
            get { lock (Sync) return _value; }
        }

        public static void Configure(int field, int value)
        {
            lock (Sync)
            {
                _field = field;
                _value = value;
            }
        }

        public static void Disable()
        {
            Configure(0, 0);
        }

        public static bool Apply(CheckStatAnswer ack)
        {
            if (ack == null) return false;

            int field;
            int value;
            lock (Sync)
            {
                field = _field;
                value = _value;
            }

            switch (field)
            {
                case 1: ack.PerformanceUnknown1 = value; break;
                case 2: ack.PerformanceUnknown2 = value; break;
                case 3: ack.PerformanceUnknown3 = value; break;
                case 4: ack.PerformanceUnknown4 = value; break;
                case 5: ack.VehicleSpeed = value; break;
                case 6: ack.VehicleDurability = value; break;
                case 7: ack.VehicleAcceleration = value; break;
                case 8: ack.VehicleBoost = value; break;
                case 9: ack.Speed = value; break;
                case 10: ack.Crash = value; break;
                case 11: ack.Accel = value; break;
                case 12: ack.Boost = value; break;
                default: return false;
            }

            QuietLog.Write("VehiclePerformanceProbe", "Applied field={0} value={1}", field, value);
            return true;
        }
    }
}
