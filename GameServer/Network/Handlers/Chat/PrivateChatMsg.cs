using System;
using System.IO;
using System.Linq;
using System.Text;
using Shared.Network;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class PrivateChatMsg
    {
        private const ushort WhisperPacketId = 148;
        private const uint WhisperModeTo = 0;
        private const uint WhisperModeFrom = 1;

        [Packet(Packets.CmdWhisper)]
        public static void Whisper(Packet packet)
        {
            if (packet.Sender?.User?.ActiveCharacter == null)
                return;

            var sender = packet.Sender.User.ActiveCharacter;
            string targetName;
            string message;
            uint clientMode;
            ushort byteLength;

            try
            {
                targetName = packet.Reader.ReadUnicodeStatic(20).Trim();
                clientMode = packet.Reader.ReadUInt32();
                byteLength = packet.Reader.ReadUInt16();

                var remaining = packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position;
                if (byteLength < 2 || (byteLength & 1) != 0 || byteLength > remaining)
                    throw new InvalidDataException(
                        string.Format("Invalid whisper byte length {0} (remaining {1}).", byteLength, remaining));

                var bytes = packet.Reader.ReadBytes(byteLength);
                message = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                Log.Warning("CmdWhisper: invalid packet from {0}: {1}", sender.Name, ex.Message);
                WriteWhisperResearch("PARSE148_ERROR", packet.Sender.User.VehicleSerial, 0,
                    sender.Name, null, null, packet, ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrEmpty(message))
                return;

            sender.LastMessageFrom = targetName;
            WriteWhisperResearch("IN148", packet.Sender.User.VehicleSerial, 0,
                sender.Name, targetName, message, packet,
                string.Format("retail Name[20] mode={0} byteLength={1}", clientMode, byteLength));

            SendWhisper(packet, targetName, message);
        }

        [Packet(Packets.CmdPrivateChatMsg)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender?.User?.ActiveCharacter == null)
                return;

            var sender = packet.Sender.User.ActiveCharacter;
            var targetName = sender.LastMessageFrom;
            string message;

            try
            {
                var stream = packet.Reader.BaseStream;
                var original = stream.Position;
                try
                {
                    message = packet.Reader.ReadUnicodePrefixed();
                    if (stream.Position > stream.Length)
                        throw new EndOfStreamException();
                }
                catch
                {
                    stream.Position = original;
                    message = ReadTrailingUnicodeMessage(stream);
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

            if (!string.IsNullOrEmpty(message))
                SendWhisper(packet, targetName, message);
        }

        private static string ReadTrailingUnicodeMessage(Stream stream)
        {
            if (stream == null || !stream.CanSeek)
                return string.Empty;

            var original = stream.Position;
            try
            {
                var end = stream.Length;
                for (var pos = original; pos + 4 <= end; pos++)
                {
                    stream.Position = pos;
                    var lo = stream.ReadByte();
                    var hi = stream.ReadByte();
                    if (lo < 0 || hi < 0)
                        break;

                    var byteLength = lo | (hi << 8);
                    if (byteLength < 2 || (byteLength & 1) != 0 || pos + 2 + byteLength != end)
                        continue;

                    var bytes = new byte[byteLength];
                    if (stream.Read(bytes, 0, bytes.Length) != bytes.Length)
                        return string.Empty;

                    return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                }

                return string.Empty;
            }
            finally
            {
                stream.Position = original;
            }
        }

        private static void SendWhisper(Packet sourcePacket, string targetName, string message)
        {
            var senderCharacter = sourcePacket.Sender.User.ActiveCharacter;
            if (sourcePacket.Sender.User.Status == UserStatus.Muted)
                return;

            var target = GameServer.Instance.Server.GetClients().FirstOrDefault(client =>
                client?.User?.ActiveCharacter != null &&
                string.Equals(client.User.ActiveCharacter.Name, targetName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                WriteWhisperResearch("TARGET_OFFLINE", sourcePacket.Sender.User.VehicleSerial, 0,
                    senderCharacter.Name, targetName, message, sourcePacket, null);
                sourcePacket.Sender.SendError(targetName + " is offline.");
                return;
            }

            // Recipient must enter the client's "from" branch, while the sender echo
            // must enter the complementary "to" branch. Sending mode 1 to both sides
            // made both lines render as "Whispering from".
            var recipientPacket = CreateWhisperAck(
                senderCharacter.Name,
                WhisperModeFrom,
                message);

            var senderEchoPacket = CreateWhisperAck(
                target.User.ActiveCharacter.Name,
                WhisperModeTo,
                message);

            target.Send(recipientPacket);
            sourcePacket.Sender.Send(senderEchoPacket);

            senderCharacter.LastMessageFrom = target.User.ActiveCharacter.Name;
            target.User.ActiveCharacter.LastMessageFrom = senderCharacter.Name;

            WriteWhisperResearch("OUT148_FROM", sourcePacket.Sender.User.VehicleSerial,
                target.User.VehicleSerial, senderCharacter.Name,
                target.User.ActiveCharacter.Name, message, recipientPacket,
                "recipient remoteName=sender modeOnWire=1 -> native FROM branch");

            WriteWhisperResearch("OUT148_TO", sourcePacket.Sender.User.VehicleSerial,
                target.User.VehicleSerial, senderCharacter.Name,
                target.User.ActiveCharacter.Name, message, senderEchoPacket,
                "sender echo remoteName=target modeOnWire=0 -> native TO branch");

            Log.Debug("Whisper: {0} -> {1}: {2}",
                senderCharacter.Name, target.User.ActiveCharacter.Name, message);
        }

        private static Packet CreateWhisperAck(string remotePlayerName, uint modeOnWire, string message)
        {
            var text = message ?? string.Empty;
            var encoded = Encoding.Unicode.GetBytes(text + "\0");

            var ack = new Packet(WhisperPacketId);
            ack.Writer.WriteUnicodeStatic(remotePlayerName ?? string.Empty, 20);
            ack.Writer.Write(modeOnWire);
            ack.Writer.Write((ushort)encoded.Length);
            ack.Writer.Write(encoded);
            return ack;
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
