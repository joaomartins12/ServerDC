using Shared.Util;

namespace Shared.Objects
{
    /// <summary>
    /// Drift City v0.77a XiPlayerInfo record. Packet 802 iterates these records with
    /// an exact 0xD8 (216-byte) stride.
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

        /*
        struct XiPlayerInfo
        {
          wchar_t Cname[13];
          unsigned __int16 Serial;
          unsigned __int16 Age;
          __int64 Cid;
          unsigned __int16 Level;
          unsigned int Exp;
          __int64 TeamId;
          __int64 TeamMarkId;
          wchar_t TeamName[14];
          unsigned __int16 TeamNLevel;
          XiVisualItem VisualItem;
          float UseTime;
          // retail record continues with 72 undocumented bytes
        };
        */

        public void Serialize(BinaryWriterExt writer)
        {
            writer.WriteUnicodeStatic(Character.Name, 13, true); // 26
            writer.Write(Serial);                               // 2
            writer.Write(Age);                                  // 2
            writer.Write(Character.Id);                         // 8
            writer.Write(Character.Level);                      // 2

            // IMPORTANT: retail XiPlayerInfo::Exp is uint32. The previous serializer
            // wrote the 64-bit BaseExp field, shifting Team/VisualItem/UseTime by four
            // bytes while still padding the record to 216. Packet 802 therefore looked
            // superficially the right size but its visual identity fields were misaligned.
            var exp = Character.ExperienceInfo.CurExp;
            if (exp < 0) exp = 0;
            writer.Write(exp > uint.MaxValue ? uint.MaxValue : (uint)exp); // 4

            if (Character.Crew == null)
                new Crew().SerializeShort(writer);               // 46
            else
                Character.Crew.SerializeShort(writer);           // 46

            writer.Write(VisualItem ?? new XiVisualItem());      // 50
            writer.Write(UseTime);                               // 4

            // 144 bytes of known fields + 72 unknown = exact 216-byte retail stride.
            writer.Write(new byte[72]);
        }
    }
}
