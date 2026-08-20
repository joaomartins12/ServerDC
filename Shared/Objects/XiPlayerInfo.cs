using System.Security;
using Shared.Util;

namespace Shared.Objects
{
    /// <summary>
    /// 216 Bytes
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
  __unaligned __declspec(align(1)) __int64 Cid;
  unsigned __int16 Level;
  unsigned int Exp;
  __unaligned __declspec(align(1)) __int64 TeamId;
  __unaligned __declspec(align(1)) __int64 TeamMarkId;
  wchar_t TeamName[14];
  unsigned __int16 TeamNLevel;
  XiVisualItem VisualItem;
  float UseTime;
};
        */
        
        /// <summary>
        /// Current client expects one XiPlayerInfo record to occupy 216 bytes.
        /// The previously serialized structure was only 208 bytes, causing the
        /// remote-player info response to end eight bytes early.
        /// </summary>
        public void Serialize(BinaryWriterExt writer)
        {
            writer.WriteUnicodeStatic(Character.Name, 13, true);
            writer.Write(Serial);
            writer.Write(Age);
            
            writer.Write(Character.Id);
            writer.Write(Character.Level);
            writer.Write(Character.ExperienceInfo.BaseExp);
            
            if (Character.Crew == null)
                new Crew().SerializeShort(writer);
            else
                Character.Crew.SerializeShort(writer);

            writer.Write(VisualItem);
            writer.Write(UseTime);

            // Preserve the undocumented tail while matching the 216-byte
            // XiPlayerInfo record size expected by PlayerInfoOldAck.
            writer.Write(new byte[68]);
        }
    }
}
