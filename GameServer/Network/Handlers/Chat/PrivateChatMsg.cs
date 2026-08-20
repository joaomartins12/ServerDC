using System;
using System.Linq;
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

            string targetName;
            try
            {
                targetName = packet.Reader.ReadUnicodeStatic(21);
            }
            catch (Exception ex)
            {
                Log.Warning("CmdWhisper: failed to read target from {0}: {1}", senderCharacter.Name, ex.Message);
                return;
            }

            targetName = (targetName ?? string.Empty).TrimEnd('\0').Trim();
            if (targetName.Length == 0)
                return;

            senderCharacter.LastMessageFrom = targetName;

            // This client carries the message body in CmdWhisper itself. After the
            // fixed 21-wchar target there is one unknown ushort and then a unicode-
            // prefixed message. Older flows may still send CmdPrivateChatMsg after
            // selecting a target, so keep that handler as a fallback below.
            string message = null;
            try
            {
                var remaining = packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position;
                if (remaining >= 4)
                {
                    packet.Reader.ReadUInt16(); // unknown / client state
                    if (packet.Reader.BaseStream.Position < packet.Reader.BaseStream.Length)
                        message = packet.Reader.ReadUnicodePrefixed();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("CmdWhisper: failed to read message from {0} to {1}: {2}",
                    senderCharacter.Name, targetName, ex.Message);
            }

            if (!string.IsNullOrEmpty(message))
                SendPrivate(packet, targetName, message);
            else
                Log.Debug("Whisper target selected: {0} -> {1}", senderCharacter.Name, targetName);
        }

        [Packet(Packets.CmdPrivateChatMsg)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender.User == null || packet.Sender.User.ActiveCharacter == null)
                return;

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
                packet.Sender.Send(new ChatMessageAnswer
                {
                    MessageType = "private",
                    SenderCharacterName = "SYSTEM",
                    Message = targetName + " is offline."
                }.CreatePacket());
                return;
            }

            var senderName = senderCharacter.Name;
            if (packet.Sender.User.GmFlag)
                senderName = "GM " + senderName;

            var ack = new ChatMessageAnswer
            {
                MessageType = "private",
                SenderCharacterName = senderName,
                Message = message ?? string.Empty
            }.CreatePacket();

            target.Send(ack);
            if (target != packet.Sender)
                packet.Sender.Send(ack);

            senderCharacter.LastMessageFrom = target.User.ActiveCharacter.Name;
            target.User.ActiveCharacter.LastMessageFrom = senderCharacter.Name;

            Log.Debug("(private) <{0}> -> <{1}> {2}", senderCharacter.Name,
                target.User.ActiveCharacter.Name, message);
        }
    }
}
