using Shared.Network;
using Shared.Network.AreaServer;

namespace AreaServer.Network.Handlers
{
    public static class AreaStatus
    {
        [Packet(Packets.CmdAreaStatus)]
        public static void Handle(Packet packet)
        {
            // The original server reports the current member count for all 100 areas.
            // Returning an all-zero array makes the client believe populated areas are
            // empty and can invalidate remote-player presence asynchronously.
            packet.Sender.Send(new AreaStatusAnswerPacket
            {
                UserCount = MoveVehicle.GetAreaUserCounts()
            }.CreatePacket());
        }
    }
}
