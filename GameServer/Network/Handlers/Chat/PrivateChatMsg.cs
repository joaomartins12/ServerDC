using System;
using System.IO;
using System.Linq;
using System.Text;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class PrivateChatMsg
    {
        [Packet(Packets.CmdWhisper)]
        public static void Whisper(Packet packet)
        {
            if (packet.Sender?.User?.ActiveCharacter == null) return;

            var sender = packet.Sender.User.ActiveCharacter;
            string targetName;
            string message;
            long messageOffset;

            try
            {
                // Captured v0.77a packet 148 starts with the native Name[10], but the
                // retail client inserts a per-message/private-chat envelope between the
                // target name and the final UTF-16 payload. Do not assume m_Len follows
                // Name[10] immediately. In the 2026-08-21 capture the payload was:
                // Name[10] + 22 envelope bytes + byteLength(6) + "oi\0".
                targetName = packet.Reader.ReadUnicodeStatic(10).Trim();

                if (!TryReadTrailingUnicodeMessage(packet.Reader.BaseStream, out message, out messageOffset))
                    message = string.Empty;
            }
            catch (Exception ex)
            {
                Log.Warning("CmdWhisper: invalid packet from {0}: {1}", sender.Name, ex.Message);
                WriteWhisperResearch("PARSE148_ERROR", packet.Sender.User.VehicleSerial, 0,
                    sender.Name, null, null, packet, ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetName)) return;

            sender.LastMessageFrom = targetName;
            WriteWhisperResearch("IN148", packet.Sender.User.VehicleSerial, 0,
                sender.Name, targetName, message, packet,
                "captured-envelope trailingMessageOffset=" + messageOffset);

            if (!string.IsNullOrEmpty(message))
                SendPrivate(packet, targetName, message);
        }

        [Packet(Packets.CmdPrivateChatMsg)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender?.User?.ActiveCharacter == null) return;

            var sender = packet.Sender.User.ActiveCharacter;
            var targetName = sender.LastMessageFrom;
            string message;
            long messageOffset;

            try
            {
                // Keep packet 149 tolerant too. Some client paths send the simple
                // ushort-character-count form, others reuse the private envelope.
                var stream = packet.Reader.BaseStream;
                var original = stream.Position;
                try
                {
                    message = packet.Reader.ReadUnicodePrefixed();
                    if (stream.Position > stream.Length)
                        throw new EndOfStreamException();
                    messageOffset = original;
                }
                catch
                {
                    stream.Position = original;
                    if (!TryReadTrailingUnicodeMessage(stream, out message, out messageOffset))
                        message = string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("CmdPrivateChatMsg: invalid packet from {0}: {1}", sender.Name, ex.Message);
                WriteWhisperResearch("PARSE149_ERROR", packet.Sender.User.VehicleSerial, 0,
                    sender.Name, targetName, null, packet, ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetName))
            {
                packet.Sender.SendError("Select a player before sending a whisper.");
                return;
            }

            WriteWhisperResearch("IN149", packet.Sender.User.VehicleSerial, 0,
                sender.Name, targetName, message, packet,
                "messageOffset=" + messageOffset);

            if (!string.IsNullOrEmpty(message))
                SendPrivate(packet, targetName, message);
        }

        private static bool TryReadTrailingUnicodeMessage(Stream stream, out string message, out long prefixOffset)
        {
            message = null;
            prefixOffset = -1;
            if (stream == null || !stream.CanSeek) return false;

            var original = stream.Position;
            try
            {
                var end = stream.Length;
                if (end - original < 4) return false;

                // Retail packet 148 uses a byte-length prefix for the final UTF-16
                // payload. Search only for a candidate that ends exactly at packet end;
                // this avoids interpreting the opaque private-chat envelope as text.
                for (var pos = original; pos + 4 <= end; pos++)
                {
                    stream.Position = pos;
                    var lo = stream.ReadByte();
                    var hi = stream.ReadByte();
                    if (lo < 0 || hi < 0) break;

                    var byteLength = lo | (hi << 8);
                    if (byteLength < 2 || (byteLength & 1) != 0) continue;
                    if (pos + 2 + byteLength != end) continue;

                    var bytes = new byte[byteLength];
                    if (stream.Read(bytes, 0, bytes.Length) != bytes.Length) return false;

                    var decoded = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                    if (decoded.IndexOf('\0') >= 0) continue;

                    message = decoded;
                    prefixOffset = pos;
                    return true;
                }

                return false;
            }
            finally
            {
                stream.Position = original;
            }
        }

        private static void SendPrivate(Packet packet, string targetName, string message)
        {
            var senderCharacter = packet.Sender.User.ActiveCharacter;
            if (packet.Sender.User.Status == UserStatus.Muted)
            {
                packet.Sender.SendError("You are currently blocked from chatting.");
                return;
            }

            var target = GameServer.Instance.Server.GetClients().FirstOrDefault(client =>
                client?.User?.ActiveCharacter != null &&
                string.Equals(client.User.ActiveCharacter.Name, targetName,
                    StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                WriteWhisperResearch("TARGET_OFFLINE", packet.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, targetName, message, packet, null);
                packet.Sender.SendError(targetName + " is offline.");
                return;
            }

            var recipientPacket = new ChatMessageAnswer
            {
                MessageType = "whisper",
                SenderCharacterName = senderCharacter.Name,
                Message = message ?? string.Empty
            }.CreatePacket();

            var senderEchoPacket = new ChatMessageAnswer
            {
                MessageType = "whisper",
                SenderCharacterName = target.User.ActiveCharacter.Name,
                Message = message ?? string.Empty
            }.CreatePacket();

            target.Send(recipientPacket);
            packet.Sender.Send(senderEchoPacket);

            senderCharacter.LastMessageFrom = target.User.ActiveCharacter.Name;
            target.User.ActiveCharacter.LastMessageFrom = senderCharacter.Name;

            WriteWhisperResearch("OUT147", packet.Sender.User.VehicleSerial,
                target.User.VehicleSerial, senderCharacter.Name,
                target.User.ActiveCharacter.Name, message, recipientPacket,
                "type=whisper nativeAck=Name[10]+Player[10]+charLen+Message");

            Log.Debug("Whisper: {0} -> {1}: {2}",
                senderCharacter.Name, target.User.ActiveCharacter.Name, message);
        }

        private static void WriteWhisperResearch(string stage, ushort sourceSerial, ushort targetSerial,
            string sender, string target, string message, Packet packet, string detail)
        {
            try
            {
                var root = AppDomain.CurrentDomain.BaseDirectory;
                var dir = Path.Combine(root, "Logs", DateTime.Now.ToString("yyyy-MM-dd"),
                    "GameServer", "Research");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "WhisperProtocol.txt");
                var hex = packet == null || packet.Buffer == null
                    ? "<no packet>"
                    : BinaryWriterExt.HexDump(packet.Buffer);

                var text = string.Format(
                    "{0:O} {1} sourceSerial={2} targetSerial={3} sender='{4}' target='{5}' message='{6}' len={7} detail='{8}'{9}{10}{9}{9}",
                    DateTime.UtcNow, stage, sourceSerial, targetSerial,
                    sender ?? string.Empty, target ?? string.Empty, message ?? string.Empty,
                    packet == null || packet.Buffer == null ? 0 : packet.Buffer.Length,
                    detail ?? string.Empty, Environment.NewLine, hex);
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
