using Shared.Util;

namespace Shared.Objects
{
    /// <summary>
    /// Drift City v0.77a world-car attributes used by Cmd_RoomNotifyChange (467).
    ///
    /// The client copies exactly four DWORDs from packet +0x08 into the vehicle's
    /// cached car-attribute block (handler 0x4C8BB0). The meaningful retail layout is:
    ///   +0x00 ushort Sort
    ///   +0x02 ushort Body
    ///   +0x04 uint   Color
    ///   +0x08 uint   Color2
    ///   +0x0C uint   State
    /// Total: 16 bytes.
    ///
    /// Older emulator code modelled only the first 8 bytes as a union. Keep the
    /// compatibility view so existing code can still populate Sort/Body/Color while
    /// exposing the two missing DWORDs required by the retail client.
    /// </summary>
    public class XiCarAttr : BinaryWriterExt.ISerializable
    {
        public class LegacyStructView
        {
            public ushort Sort;
            public ushort Body;
            public char[] Color = new char[4];
        }

        public class LegacyIntView
        {
            public int lvalSortBody;
            public int lvalColor;
        }

        public class LegacyUnionView
        {
            public LegacyStructView __s0 = new LegacyStructView();
            public LegacyIntView __s1 = new LegacyIntView();
            public long llval;
        }

        public LegacyUnionView ___u0 = new LegacyUnionView();

        /// <summary>Second car colour/material value. For window tint this is RGB565.</summary>
        public uint Color2;

        /// <summary>
        /// Retail-generated player-car attributes use state 1. The client stores this
        /// fourth DWORD together with the other three attribute DWORDs.
        /// </summary>
        public uint State = 1;

        public ushort Sort
        {
            get { return ___u0.__s0.Sort; }
            set
            {
                ___u0.__s0.Sort = value;
                RepackLegacy();
            }
        }

        public ushort Body
        {
            get { return ___u0.__s0.Body; }
            set
            {
                ___u0.__s0.Body = value;
                RepackLegacy();
            }
        }

        public uint Color
        {
            get { return unchecked((uint)___u0.__s1.lvalColor); }
            set
            {
                ___u0.__s1.lvalColor = unchecked((int)value);
                ___u0.llval = unchecked((long)((ulong)(uint)___u0.__s1.lvalSortBody | ((ulong)value << 32)));
            }
        }

        public void Serialize(BinaryWriterExt writer)
        {
            // The first 8 bytes preserve the original union representation.
            writer.Write(___u0.llval);
            writer.Write(Color2);
            writer.Write(State);
        }

        private void RepackLegacy()
        {
            var sortBody = (uint)___u0.__s0.Sort | ((uint)___u0.__s0.Body << 16);
            ___u0.__s1.lvalSortBody = unchecked((int)sortBody);
            ___u0.llval = unchecked((long)((ulong)sortBody | ((ulong)(uint)___u0.__s1.lvalColor << 32)));
        }
    }
}
