using Shared.Util;

namespace Shared.Objects
{
    /// <summary>
    /// Drift City v0.77a XiVisualItem.
    ///
    /// The retail client consumes this structure as exactly 0x38 (56) bytes.
    /// Its category dispatcher (sub_54CC80) proves that the legacy 50-byte DCNC
    /// definition was six bytes short: offsets +0x06 and +0x0A are 32-bit values,
    /// and the 9-wchar plate text starts at +0x26 rather than +0x20.
    /// </summary>
    public class XiVisualItem : BinaryWriterExt.ISerializable
    {
        public ushort Slot00;
        public ushort Slot02;
        public ushort Slot04;
        public uint Value06;
        public uint Value0A;
        public ushort Slot0E;
        public ushort Slot10;
        public ushort Slot12;
        public ushort Slot14;
        public ushort Slot16;
        public ushort Slot18;
        public ushort Slot1A;
        public ushort Slot1C;
        public ushort Slot1E;
        public ushort Slot20;
        public ushort Slot22;
        public ushort Slot24;
        public string PlateString = string.Empty;

        // Compatibility aliases for the names inherited from the old emulator.
        // These aliases now point at the offsets proven by the retail category
        // dispatcher instead of preserving the old (incorrect) packed layout.
        public short Neon { get { return unchecked((short)Slot00); } set { Slot00 = unchecked((ushort)value); } }
        public short Plate { get { return unchecked((short)Slot02); } set { Slot02 = unchecked((ushort)value); } }
        public short Decal { get { return unchecked((short)Slot04); } set { Slot04 = unchecked((ushort)value); } }
        public short DecalColor { get { return unchecked((short)Value06); } set { Value06 = unchecked((ushort)value); } }
        public short AeroBumper { get { return unchecked((short)Slot0E); } set { Slot0E = unchecked((ushort)value); } }
        public short AeroIntercooler { get { return unchecked((short)Slot10); } set { Slot10 = unchecked((ushort)value); } }
        public short AeroSet { get { return unchecked((short)Slot12); } set { Slot12 = unchecked((ushort)value); } }
        public short MufflerFlame { get { return unchecked((short)Slot14); } set { Slot14 = unchecked((ushort)value); } }
        public short Wheel { get { return unchecked((short)Slot16); } set { Slot16 = unchecked((ushort)value); } }
        public short Spoiler { get { return unchecked((short)Slot18); } set { Slot18 = unchecked((ushort)value); } }

        public void Serialize(BinaryWriterExt writer)
        {
            writer.Write(Slot00);       // +0x00
            writer.Write(Slot02);       // +0x02
            writer.Write(Slot04);       // +0x04
            writer.Write(Value06);      // +0x06
            writer.Write(Value0A);      // +0x0A
            writer.Write(Slot0E);       // +0x0E
            writer.Write(Slot10);       // +0x10
            writer.Write(Slot12);       // +0x12
            writer.Write(Slot14);       // +0x14
            writer.Write(Slot16);       // +0x16
            writer.Write(Slot18);       // +0x18
            writer.Write(Slot1A);       // +0x1A
            writer.Write(Slot1C);       // +0x1C
            writer.Write(Slot1E);       // +0x1E
            writer.Write(Slot20);       // +0x20
            writer.Write(Slot22);       // +0x22
            writer.Write(Slot24);       // +0x24
            writer.WriteUnicodeStatic(PlateString ?? string.Empty, 9); // +0x26 .. +0x37
        }
    }
}
