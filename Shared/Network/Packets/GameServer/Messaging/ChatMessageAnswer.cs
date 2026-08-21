using System.IO;
using Shared.Network.AreaServer;
using Shared.Util;

namespace Shared.Network.GameServer
{
    /// <summary>
    /// Drift City v0.77a retail BS_PktChatMsgAck (packet 147 / sub_539050).
    ///
    /// Native layout after the packet id:
    ///   wchar_t m_Name[10];
    ///   wchar_t m_Player[18];
    ///   ushort  m_Len;
    ///   wchar_t m_Message[m_Len];
    ///
    /// The client handler reads m_Name at +0x02, m_Player at +0x16 and
    /// m_Message at +0x3C. Keep this serializer intact for normal/channel/server
    /// chat; private whispers use the dedicated packet 150 path.
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

        public override int ExpectedSize() => 2 * ((Message ?? string.Empty).Length + 30);

        public override byte[] GetBytes()
        {
            using (var ms = new MemoryStream())
            using (var bs = new BinaryWriterExt(ms))
            {
                bs.WriteUnicodeStatic(MessageType ?? string.Empty, 10);
                bs.WriteUnicodeStatic(SenderCharacterName ?? string.Empty, 18);
                bs.WriteUnicode(Message ?? string.Empty);
                return ms.ToArray();
            }
        }
    }
}
