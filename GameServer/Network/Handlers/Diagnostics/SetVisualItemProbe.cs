using System;
using Shared.Network;
using Shared.Util;

namespace GameServer.Network.Handlers.Diagnostics
{
    /// <summary>
    /// Passive protocol probe for retail CmdSetVisualItem (805).
    ///
    /// The v0.77 client exposes this command in its client->server dispatch table, but
    /// its wire structure is not yet fully mapped. Do not answer or mutate game state
    /// here: logging the untouched payload lets retail captures reveal whether this is
    /// the missing Visual Shop preview/paint commit path.
    /// </summary>
    public static class SetVisualItemProbe
    {
        [Packet(Packets.CmdSetVisualItem)]
        public static void Handle(Packet packet)
        {
            var payload = packet == null || packet.Buffer == null ? new byte[0] : packet.Buffer;
            var character = packet?.Sender?.User?.ActiveCharacter;

            Log.Info(
                "CmdSetVisualItem(805) PROBE: CID={0} Name={1} PayloadLen={2} HEX={3}",
                character == null ? 0UL : character.Id,
                character == null ? "UNKNOWN" : character.Name,
                payload.Length,
                payload.Length == 0 ? "<empty>" : BitConverter.ToString(payload).Replace("-", " "));

            // Intentionally no ACK. Until the exact retail structure/response path is
            // proven, fabricating a reply can corrupt the client's Visual Shop state.
        }
    }
}
