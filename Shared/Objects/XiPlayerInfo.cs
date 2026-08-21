using Shared.Util;

namespace Shared.Objects
{
    /// <summary>
    /// Drift City v0.77a XiPlayerInfo record. Packet 802/809 iterate records with
    /// an exact 0xD8 (216-byte) stride.
    ///
    /// The retail 802 handler copies the first 0x96 (150) bytes into its live
    /// player structure. Those 150 bytes are: 90 bytes of identity/crew data,
    /// a 0x38 (56-byte) XiVisualItem and a 4-byte UseTime. The remaining 66 bytes
    /// are opaque tail data. Keeping this boundary exact is important because the
    /// old 50-byte XiVisualItem shifted every field after visual offset +0x20.
    /// </summary>
    public class XiPlayerInfo : BinaryWriterExt.ISerializable
    {
        public Character Character;
        public ushort Serial;
        public ushort Age;
        public XiVisualItem VisualItem;
        public float UseTime;

        public XiPlayerInfo()
        {
            Character = new Character();
            Serial = 0;
            Age = 0;
            VisualItem = new XiVisualItem();
        }

        public XiPlayerInfo(ushort vehicleSerial, Character character)
        {
            Character = character;
            Serial = vehicleSerial;
            Age = 0;
            VisualItem = new XiVisualItem();
        }

        public void Serialize(BinaryWriterExt writer)
        {
            writer.WriteUnicodeStatic(Character.Name, 13, true); // 26
            writer.Write(Serial);                               // 2
            writer.Write(Age);                                  // 2
            writer.Write(Character.Id);                         // 8
            writer.Write(Character.Level);                      // 2

            // Retail XiPlayerInfo::Exp is uint32.
            var exp = Character.ExperienceInfo.CurExp;
            if (exp < 0) exp = 0;
            writer.Write(exp > uint.MaxValue ? uint.MaxValue : (uint)exp); // 4

            if (Character.Crew == null)
                new Crew().SerializeShort(writer);               // 46
            else
                Character.Crew.SerializeShort(writer);           // 46

            // Offset 0x5A (90), exact retail XiVisualItem size 0x38 (56).
            writer.Write(VisualItem ?? new XiVisualItem());      // 56
            writer.Write(UseTime);                               // 4

            // Known retail prefix is 0x96 (150) bytes. 150 + 66 = 216.
            writer.Write(new byte[66]);
        }
    }
}
