using System.IO;
using System.Text;
using Shared.Network.AreaServer;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// Drift City v0.77a retail BS_PktChatMsgAck (packet 147).
    ///
    /// Captured wire layout:
    ///   wchar_t m_Name[10];
    ///   wchar_t m_Player[10];
    ///   wchar_t m_Message[]; // NUL terminated, NO length prefix
    ///
    /// The previous implementation used WriteUnicode(Message), which inserted a
    /// ushort before the text. Retail clients interpreted that ushort as the first
    /// character of the message, making whispers blank/garbled.
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

        public override int ExpectedSize() => 42 + ((Message ?? string.Empty).Length + 1) * 2;

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.WriteUnicodeStatic(MessageType ?? string.Empty, 10);
                bs.WriteUnicodeStatic(SenderCharacterName ?? string.Empty, 10);

                // Retail packet 147 carries the message directly after the two
                // fixed wchar arrays. Write the terminating UTF-16 NUL explicitly.
                var text = Encoding.Unicode.GetBytes((Message ?? string.Empty) + "\0");
                bs.Write(text);
                return ms.ToArray();
            }
        }
    }
}
