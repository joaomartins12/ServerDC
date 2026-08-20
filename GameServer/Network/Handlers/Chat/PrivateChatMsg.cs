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
            if (packet.Sender.User == null || packet.Sender.User.ActiveCharacter == null)
                return;

            var senderCharacter = packet.Sender.User.ActiveCharacter;
            WriteWhisperResearch("IN148", packet.Sender.User.VehicleSerial, 0,
                senderCharacter.Name, null, null, packet);

            var stream = packet.Reader.BaseStream;
            string targetName;
            try
            {
                targetName = ReadNullTerminatedUnicode(packet);
            }
            catch (Exception ex)
            {
                Log.Warning("CmdWhisper: failed to read target from {0}: {1}", senderCharacter.Name, ex.Message);
                WriteWhisperResearch("PARSE_TARGET_ERROR", packet.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, null, ex.Message, packet);
                return;
            }

            targetName = (targetName ?? string.Empty).Trim();
            if (targetName.Length == 0)
                return;

            senderCharacter.LastMessageFrom = targetName;

            string message;
            if (!TryReadTrailingUnicodeMessage(stream, out message) || string.IsNullOrEmpty(message))
            {
                Log.Debug("Whisper target selected without message: {0} -> {1}", senderCharacter.Name, targetName);
                WriteWhisperResearch("NO_MESSAGE", packet.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, targetName, null, packet);
                return;
            }

            WriteWhisperResearch("PARSED148", packet.Sender.User.VehicleSerial, 0,
                senderCharacter.Name, targetName, message, packet);
            SendPrivate(packet, targetName, message);
        }

        [Packet(Packets.CmdPrivateChatMsg)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender.User == null || packet.Sender.User.ActiveCharacter == null)
                return;

            WriteWhisperResearch("IN149", packet.Sender.User.VehicleSerial, 0,
                packet.Sender.User.ActiveCharacter.Name, packet.Sender.User.ActiveCharacter.LastMessageFrom, null, packet);

            var targetName = packet.Sender.User.ActiveCharacter.LastMessageFrom;
            string message;
            try
            {
                message = packet.Reader.ReadUnicodePrefixed();
            }
            catch (Exception ex)
            {
                Log.Warning("CmdPrivateChatMsg: failed to read message from {0}: {1}",
                    packet.Sender.User.ActiveCharacter.Name, ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetName))
            {
                packet.Sender.SendError("Select a player before sending a whisper.");
                return;
            }

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

        private static bool TryReadTrailingUnicodeMessage(Stream stream, out string message)
        {
            message = null;
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
                    if (byteLength < 2 || (byteLength & 1) != 0)
                        continue;
                    if (pos + 2 + byteLength != end)
                        continue;

                    var bytes = new byte[byteLength];
                    var read = stream.Read(bytes, 0, bytes.Length);
                    if (read != bytes.Length)
                        return false;

                    message = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
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

            var target = GameServer.Instance.Server.GetClients()
                .FirstOrDefault(client =>
                    client != null &&
                    client.User != null &&
                    client.User.ActiveCharacter != null &&
                    string.Equals(client.User.ActiveCharacter.Name, targetName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                WriteWhisperResearch("TARGET_OFFLINE", packet.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, targetName, message, packet);
                packet.Sender.SendError(targetName + " is offline.");
                return;
            }

            var senderName = senderCharacter.Name;
            if (packet.Sender.User.GmFlag)
                senderName = "GM " + senderName;

            var ack = new ChatMessageAnswer
            {
                MessageType = "whisper",
                SenderCharacterName = senderName,
                Message = message ?? string.Empty
            }.CreatePacket();

            WriteWhisperResearch("OUT147_WHISPER", packet.Sender.User.VehicleSerial,
                target.User.VehicleSerial, senderCharacter.Name, target.User.ActiveCharacter.Name, message, ack);
            target.Send(ack);

            senderCharacter.LastMessageFrom = target.User.ActiveCharacter.Name;
            target.User.ActiveCharacter.LastMessageFrom = senderCharacter.Name;

            Log.Debug("Whisper delivered: <{0}> -> <{1}> {2}", senderCharacter.Name,
                target.User.ActiveCharacter.Name, message);
        }

        private static void WriteWhisperResearch(string stage, ushort sourceSerial, ushort targetSerial,
            string sender, string target, string message, Packet packet)
        {
            try
            {
                var dir = Path.Combine("Logs", "Research");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "WhisperProtocol.txt");
                var hex = packet == null || packet.Buffer == null
                    ? "<no packet>"
                    : BinaryWriterExt.HexDump(packet.Buffer);

                var text = string.Format(
                    "{0:O} {1} sourceSerial={2} targetSerial={3} sender='{4}' target='{5}' message='{6}' len={7}{8}{9}{8}{8}",
                    DateTime.UtcNow, stage, sourceSerial, targetSerial,
                    sender ?? string.Empty, target ?? string.Empty, message ?? string.Empty,
                    packet == null || packet.Buffer == null ? 0 : packet.Buffer.Length,
                    Environment.NewLine, hex);
                File.AppendAllText(path, text);
            }
            catch
            {
            }
        }
    }
}
