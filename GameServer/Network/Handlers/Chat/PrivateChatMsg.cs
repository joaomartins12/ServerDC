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
        /// <summary>
        /// Selects the character that subsequent CmdPrivateChatMsg packets are sent to.
        /// The client uses a separate packet for the whisper target and the message body.
        /// </summary>
        [Packet(Packets.CmdWhisper)]
        public static void Whisper(Packet packet)
        {
            if (packet.Sender.User == null || packet.Sender.User.ActiveCharacter == null)
                return;

            string target;
            try
            {
                var remaining = packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position;
                target = remaining >= 42
                    ? packet.Reader.ReadUnicodeStatic(21)
                    : packet.Reader.ReadUnicodePrefixed();
            }
            catch (Exception ex)
            {
                Log.Warning("CmdWhisper: failed to read target: {0}", ex.Message);
                return;
            }

            target = (target ?? string.Empty).TrimEnd('\0').Trim();
            if (target.Length == 0)
                return;

            packet.Sender.User.ActiveCharacter.LastMessageFrom = target;
            Log.Debug("Whisper target: {0} -> {1}", packet.Sender.User.ActiveCharacter.Name, target);
        }

        [Packet(Packets.CmdPrivateChatMsg)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender.User == null || packet.Sender.User.ActiveCharacter == null)
                return;

            var senderCharacter = packet.Sender.User.ActiveCharacter;
            var targetName = senderCharacter.LastMessageFrom;

            string message;
            try
            {
                message = packet.Reader.ReadUnicodePrefixed();
            }
            catch (Exception ex)
            {
                Log.Warning("CmdPrivateChatMsg: failed to read message from {0}: {1}", senderCharacter.Name, ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(targetName))
            {
                packet.Sender.SendError("Select a player before sending a whisper.");
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
                var offline = new ChatMessageAnswer
                {
                    MessageType = "private",
                    SenderCharacterName = "SYSTEM",
                    Message = targetName + " is offline."
                }.CreatePacket();
                packet.Sender.Send(offline);
                return;
            }

            if (packet.Sender.User.Status == UserStatus.Muted)
            {
                packet.Sender.SendError("You are currently blocked from chatting.");
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

            // Make replying work naturally: the recipient's next private message targets
            // the character that just whispered them.
            target.User.ActiveCharacter.LastMessageFrom = senderCharacter.Name;

            Log.Debug("(private) <{0}> -> <{1}> {2}", senderCharacter.Name,
                target.User.ActiveCharacter.Name, message);
        }
    }
}
