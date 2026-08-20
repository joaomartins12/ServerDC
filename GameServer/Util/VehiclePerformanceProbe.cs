using System;
using System.Globalization;
using Shared.Network.GameServer;

namespace GameServer.Util
{
    /// <summary>
    /// Runtime StatUpdate probe used to map the current client's vehicle-performance UI.
    /// Fields are consecutive 4-byte slots starting at payload offset 0x50:
    /// 1-8 unknown/stat-info, 9-13 enchant integer fields, 14-17 known float-shaped
    /// enchant fields, and 18-19 the final 8 unknown bytes.
    ///
    /// The probe always writes the requested raw 4 bytes. In float mode the supplied
    /// decimal is converted to IEEE-754 single-precision bits first, which lets us test
    /// even slots currently declared as int without changing packet offsets.
    /// </summary>
    internal static class VehiclePerformanceProbe
    {
        internal enum ProbeMode
        {
            Int,
            Float
        }

        private static readonly object Sync = new object();
        private static int _field;
        private static int _rawValue;
        private static float _displayFloat;
        private static ProbeMode _mode;

        public static int Field
        {
            get { lock (Sync) return _field; }
        }

        public static int RawValue
        {
            get { lock (Sync) return _rawValue; }
        }

        public static ProbeMode Mode
        {
            get { lock (Sync) return _mode; }
        }

        public static float FloatValue
        {
            get { lock (Sync) return _displayFloat; }
        }

        public static void ConfigureInt(int field, int value)
        {
            lock (Sync)
            {
                _field = field;
                _rawValue = value;
                _displayFloat = RawIntToFloat(value);
                _mode = ProbeMode.Int;
            }
        }

        public static void ConfigureFloat(int field, float value)
        {
            lock (Sync)
            {
                _field = field;
                _displayFloat = value;
                _rawValue = FloatToRawInt(value);
                _mode = ProbeMode.Float;
            }
        }

        // Backwards compatibility for any existing caller.
        public static void Configure(int field, int value)
        {
            ConfigureInt(field, value);
        }

        public static void Disable()
        {
            lock (Sync)
            {
                _field = 0;
                _rawValue = 0;
                _displayFloat = 0.0f;
                _mode = ProbeMode.Int;
            }
        }

        public static bool Apply(CheckStatAnswer ack)
        {
            if (ack == null) return false;

            int field;
            int rawValue;
            float displayFloat;
            ProbeMode mode;
            lock (Sync)
            {
                field = _field;
                rawValue = _rawValue;
                displayFloat = _displayFloat;
                mode = _mode;
            }

            if (!ApplyRawSlot(ack, field, rawValue))
                return false;

            QuietLog.Write("VehiclePerformanceProbe",
                "Applied field={0} mode={1} rawInt={2} rawHex=0x{3:X8} float={4}",
                field,
                mode,
                rawValue,
                unchecked((uint)rawValue),
                displayFloat.ToString("R", CultureInfo.InvariantCulture));
            return true;
        }

        private static bool ApplyRawSlot(CheckStatAnswer ack, int field, int rawValue)
        {
            var asFloat = RawIntToFloat(rawValue);

            switch (field)
            {
                case 1: ack.PerformanceUnknown1 = rawValue; break;
                case 2: ack.PerformanceUnknown2 = rawValue; break;
                case 3: ack.PerformanceUnknown3 = rawValue; break;
                case 4: ack.PerformanceUnknown4 = rawValue; break;
                case 5: ack.VehicleSpeed = rawValue; break;
                case 6: ack.VehicleDurability = rawValue; break;
                case 7: ack.VehicleAcceleration = rawValue; break;
                case 8: ack.VehicleBoost = rawValue; break;
                case 9: ack.Speed = rawValue; break;
                case 10: ack.Crash = rawValue; break;
                case 11: ack.Accel = rawValue; break;
                case 12: ack.Boost = rawValue; break;
                case 13: ack.AddSpeed = rawValue; break;
                case 14: ack.Drop = asFloat; break;
                case 15: ack.Exp = asFloat; break;
                case 16: ack.MitronCapacity = asFloat; break;
                case 17: ack.MitronEfficiency = asFloat; break;
                case 18: ack.TrailingUnknown1 = rawValue; break;
                case 19: ack.TrailingUnknown2 = rawValue; break;
                default: return false;
            }

            return true;
        }

        private static int FloatToRawInt(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static float RawIntToFloat(int value)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
        }
    }
}
