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
        private static readonly object ProbeSync = new object();
        private static string _deliveryMode = "private";

        public static string DeliveryMode
        {
            get
            {
                lock (ProbeSync)
                    return _deliveryMode;
            }
        }

        public static bool ConfigureDeliveryMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return false;
            mode = mode.Trim().ToLowerInvariant();
            switch (mode)
            {
                case "whisper":
                case "private":
                case "normal":
                case "channel":
                case "149":
                case "off":
                    lock (ProbeSync) _deliveryMode = mode;
                    WriteWhisperResearch("PROBE_MODE", 0, 0, null, null, null, null, "mode=" + mode);
                    return true;
                default:
                    return false;
            }
        }

        [Packet(Packets.CmdWhisper)]
        public static void Whisper(Packet packet)
        {
            if (packet.Sender.User == null || packet.Sender.User.ActiveCharacter == null) return;

            var senderCharacter = packet.Sender.User.ActiveCharacter;
            WriteWhisperResearch("IN148", packet.Sender.User.VehicleSerial, 0,
                senderCharacter.Name, null, null, packet, "mode=" + DeliveryMode);

            var stream = packet.Reader.BaseStream;
            string targetName;
            try { targetName = ReadNullTerminatedUnicode(packet); }
            catch (Exception ex)
            {
                Log.Warning("CmdWhisper: failed to read target from {0}: {1}", senderCharacter.Name, ex.Message);
                WriteWhisperResearch("PARSE_TARGET_ERROR", packet.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, null, ex.Message, packet, null);
                return;
            }

            targetName = (targetName ?? string.Empty).Trim();
            if (targetName.Length == 0) return;
            senderCharacter.LastMessageFrom = targetName;

            var targetEndOffset = stream.CanSeek ? stream.Position : -1;
            string message;
            long messagePrefixOffset;
            if (!TryReadTrailingUnicodeMessage(stream, out message, out messagePrefixOffset) || string.IsNullOrEmpty(message))
            {
                Log.Debug("Whisper target selected without message: {0} -> {1}", senderCharacter.Name, targetName);
                WriteWhisperResearch("NO_MESSAGE", packet.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, targetName, null, packet, "targetEnd=" + targetEndOffset);
                return;
            }

            var metadataBytes = targetEndOffset >= 0 && messagePrefixOffset >= targetEndOffset
                ? messagePrefixOffset - targetEndOffset : -1;

            WriteWhisperResearch("PARSED148", packet.Sender.User.VehicleSerial, 0,
                senderCharacter.Name, targetName, message, packet,
                "targetEnd=" + targetEndOffset + " messagePrefix=" + messagePrefixOffset +
                " metadataBytes=" + metadataBytes + " mode=" + DeliveryMode);
            SendPrivate(packet, targetName, message);
        }

        [Packet(Packets.CmdPrivateChatMsg)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender.User == null || packet.Sender.User.ActiveCharacter == null) return;

            WriteWhisperResearch("IN149", packet.Sender.User.VehicleSerial, 0,
                packet.Sender.User.ActiveCharacter.Name,
                packet.Sender.User.ActiveCharacter.LastMessageFrom, null, packet,
                "mode=" + DeliveryMode);

            var targetName = packet.Sender.User.ActiveCharacter.LastMessageFrom;
            string message;
            try { message = packet.Reader.ReadUnicodePrefixed(); }
            catch (Exception ex)
            {
                Log.Warning("CmdPrivateChatMsg: failed to read message from {0}: {1}",
                    packet.Sender.User.ActiveCharacter.Name, ex.Message);
                WriteWhisperResearch("PARSE149_ERROR", packet.Sender.User.VehicleSerial, 0,
                    packet.Sender.User.ActiveCharacter.Name, targetName, ex.Message, packet, null);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetName))
            {
                packet.Sender.SendError("Select a player before sending a whisper.");
                return;
            }

            WriteWhisperResearch("PARSED149", packet.Sender.User.VehicleSerial, 0,
                packet.Sender.User.ActiveCharacter.Name, targetName, message, packet, null);
            SendPrivate(packet, targetName, message);
        }

        private static string ReadNullTerminatedUnicode(Packet packet)
        {
            var sb = new StringBuilder();
            while (packet.Reader.BaseStream.Position + 1 < packet.Reader.BaseStream.Length)
            {
                var c = packet.Reader.ReadUInt16();
                if (c == 0) break;
                sb.Append((char)c);
            }
            return sb.ToString();
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

                for (var pos = original; pos + 4 <= end; pos += 2)
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

                    message = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                    prefixOffset = pos;
                    return true;
                }
                return false;
            }
            finally { stream.Position = original; }
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
                client != null && client.User != null && client.User.ActiveCharacter != null &&
                string.Equals(client.User.ActiveCharacter.Name, targetName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                WriteWhisperResearch("TARGET_OFFLINE", packet.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, targetName, message, packet, null);
                packet.Sender.SendError(targetName + " is offline.");
                return;
            }

            var senderName = senderCharacter.Name;
            if (packet.Sender.User.GmFlag) senderName = "GM " + senderName;

            var targetDisplayName = target.User.ActiveCharacter.Name;
            if (target.User.GmFlag) targetDisplayName = "GM " + targetDisplayName;

            var mode = DeliveryMode;
            if (mode == "off")
            {
                WriteWhisperResearch("DELIVERY_DISABLED", packet.Sender.User.VehicleSerial,
                    target.User.VehicleSerial, senderCharacter.Name,
                    target.User.ActiveCharacter.Name, message, null, "mode=off");
                return;
            }

            // Keep MessageType=private because this is the client-proven whisper route.
            // The client itself renders SenderCharacterName as [Name]:, so use "Whisper"
            // there and put direction/player/message in the body. Result:
            // [Whisper]: [From] [Portuga] (message)
            // [Whisper]: [To]   [Port]    (message)
            var rawMessage = message ?? string.Empty;
            var recipientDisplayMessage = "[From] [" + senderName + "] (" + rawMessage + ")";
            var senderDisplayMessage = "[To] [" + targetDisplayName + "] (" + rawMessage + ")";

            Packet recipientPacket;
            Packet senderEchoPacket;
            string stage;

            if (mode == "149")
            {
                recipientPacket = new Packet(Packets.CmdPrivateChatMsg);
                recipientPacket.Writer.WriteUnicodeStatic("Whisper", 21, true);
                recipientPacket.Writer.WriteUnicode(recipientDisplayMessage);

                senderEchoPacket = new Packet(Packets.CmdPrivateChatMsg);
                senderEchoPacket.Writer.WriteUnicodeStatic("Whisper", 21, true);
                senderEchoPacket.Writer.WriteUnicode(senderDisplayMessage);
                stage = "OUT149";
            }
            else
            {
                recipientPacket = new ChatMessageAnswer
                {
                    MessageType = mode,
                    SenderCharacterName = "Whisper",
                    Message = recipientDisplayMessage
                }.CreatePacket();

                senderEchoPacket = new ChatMessageAnswer
                {
                    MessageType = mode,
                    SenderCharacterName = "Whisper",
                    Message = senderDisplayMessage
                }.CreatePacket();
                stage = "OUT147_" + mode.ToUpperInvariant();
            }

            WriteWhisperResearch(stage, packet.Sender.User.VehicleSerial,
                target.User.VehicleSerial, senderCharacter.Name,
                target.User.ActiveCharacter.Name, message, recipientPacket, "mode=" + mode + " direction=recipient");
            target.Send(recipientPacket);

            WriteWhisperResearch(stage + "_ECHO", packet.Sender.User.VehicleSerial,
                packet.Sender.User.VehicleSerial, target.User.ActiveCharacter.Name,
                senderCharacter.Name, message, senderEchoPacket, "mode=" + mode + " direction=sender");
            packet.Sender.Send(senderEchoPacket);

            senderCharacter.LastMessageFrom = target.User.ActiveCharacter.Name;
            target.User.ActiveCharacter.LastMessageFrom = senderCharacter.Name;
            Log.Debug("Whisper delivered mode={0}: <{1}> -> <{2}> {3}", mode,
                senderCharacter.Name, target.User.ActiveCharacter.Name, message);
        }

        private static void WriteWhisperResearch(string stage, ushort sourceSerial, ushort targetSerial,
            string sender, string target, string message, Packet packet, string detail)
        {
            try
            {
                var root = AppDomain.CurrentDomain.BaseDirectory;
                var dir = Path.Combine(root, "Logs", DateTime.Now.ToString("yyyy-MM-dd"), "GameServer", "Research");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "WhisperProtocol.txt");
                var hex = packet == null || packet.Buffer == null ? "<no packet>" : BinaryWriterExt.HexDump(packet.Buffer);

                var text = string.Format(
                    "{0:O} {1} sourceSerial={2} targetSerial={3} sender='{4}' target='{5}' message='{6}' len={7} detail='{8}'{9}{10}{9}{9}",
                    DateTime.UtcNow, stage, sourceSerial, targetSerial,
                    sender ?? string.Empty, target ?? string.Empty, message ?? string.Empty,
                    packet == null || packet.Buffer == null ? 0 : packet.Buffer.Length,
                    detail ?? string.Empty, Environment.NewLine, hex);
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch { }
        }
    }
}
