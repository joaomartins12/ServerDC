using Shared.Network;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    /// <summary>
    /// Initializes the sticker subsystem for the current character.
    /// Persistent sticker records are not implemented yet, so return an explicit
    /// empty list instead of leaving packet 1350 unanswered.
    /// </summary>
    public class MyStickerList
    {
        [Packet((ushort)1350)]
        public static void Handle(Packet packet)
        {
            var ack = new Packet((ushort)1351);
            ack.Writer.Write(0); // Sticker count.
            packet.Sender.Send(ack);

            var character = packet.Sender.User?.ActiveCharacter;
            Log.Debug(
                "MyStickerListAck: CID={0} Name={1} Count=0",
                character == null ? 0UL : character.Id,
                character == null ? "UNKNOWN" : character.Name);
        }
    }
}
