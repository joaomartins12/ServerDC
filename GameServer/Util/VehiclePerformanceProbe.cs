using Shared.Network.GameServer;

namespace GameServer.Util
{
    /// <summary>
    /// Temporary runtime probe used to map the ten unknown StatUpdate performance fields
    /// against the four values displayed by the current client. Field 0 disables probing.
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
                case 9: ack.PerformanceUnknown9 = value; break;
                case 10: ack.PerformanceUnknown10 = value; break;
                default: return false;
            }

            QuietLog.Write("VehiclePerformanceProbe", "Applied field={0} value={1}", field, value);
            return true;
        }
    }
}
