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
        /// <summary>
        /// Packet 148, BS_PktWhisper in the v0.77a client:
        ///   wchar_t m_Name[10];
        ///   ushort  m_Len;
        ///   wchar_t message[m_Len];
        ///
        /// m_Name is the target character name. The old implementation treated
        /// it as a variable/null-terminated string and then searched the rest of
        /// the packet for a plausible message prefix, which shifted the reader.
        /// </summary>
        [Packet(Packets.CmdWhisper)]
        public static void Whisper(Packet packet)
        {
            if (packet.Sender?.User?.ActiveCharacter == null) return;

            var sender = packet.Sender.User.ActiveCharacter;
            string targetName;
            string message;

            try
            {
                targetName = packet.Reader.ReadUnicodeStatic(10).Trim();
                message = packet.Reader.ReadUnicodePrefixed();
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
                sender.Name, targetName, message, packet, "native=Name[10]+Len+Message");

            if (!string.IsNullOrEmpty(message))
                SendPrivate(packet, targetName, message);
        }

        /// <summary>
        /// Packet 149 contains the text for the already selected private-chat
        /// target. The client does not repeat the target name in this packet.
        /// </summary>
        [Packet(Packets.CmdPrivateChatMsg)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender?.User?.ActiveCharacter == null) return;

            var sender = packet.Sender.User.ActiveCharacter;
            var targetName = sender.LastMessageFrom;
            string message;

            try
            {
                message = packet.Reader.ReadUnicodePrefixed();
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
                sender.Name, targetName, message, packet, "native=Len+Message");

            if (!string.IsNullOrEmpty(message))
                SendPrivate(packet, targetName, message);
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

            // The native visual response is packet 147 (BS_PktChatMsgAck):
            // Name[10]="whisper", Player[10], Len, Message.
            var recipientPacket = new ChatMessageAnswer
            {
                MessageType = "whisper",
                SenderCharacterName = senderCharacter.Name,
                Message = message ?? string.Empty
            }.CreatePacket();

            // The sender receives the same native packet with the other player's
            // name, so the client renders the outgoing whisper coherently too.
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
                "type=whisper native=Name[10]+Player[10]+Len+Message");

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
