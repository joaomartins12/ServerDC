using System.IO;
using Shared.Network.AreaServer;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// Drift City v0.77a BS_PktChatMsgAck (packet 147).
    ///
    /// Native client layout:
    ///   wchar_t m_Name[10];
    ///   wchar_t m_Player[10];
    ///   ushort  m_Len;
    ///   wchar_t m_Message[m_Len];
    ///
    /// BinaryWriterExt.WriteUnicode writes the ushort length followed by the
    /// UTF-16 message (including the terminating NUL on the wire), matching the
    /// existing client packet convention.
    /// </summary>
    public class ChatMessageAnswer : OutPacket
    {
        public string MessageType;
        public string SenderCharacterName;
        public string Message = "MESSAGE";

        public override Packet CreatePacket()
        {
            return base.CreatePacket(Packets.ChatMsgAck);
        }

        public override int ExpectedSize() => 2 * (Message.Length + 22);

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                // BS_PktChatMsgAck::m_Name[10]
                bs.WriteUnicodeStatic(MessageType, 10);

                // BS_PktChatMsgAck::m_Player[10]
                bs.WriteUnicodeStatic(SenderCharacterName, 10);

                // BS_PktChatMsgAck::m_Len + trailing UTF-16 message
                bs.WriteUnicode(Message);
                return ms.ToArray();
            }
        }
    }
}
