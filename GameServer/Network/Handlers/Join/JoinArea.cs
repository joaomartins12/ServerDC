using System;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class JoinArea
    {
        [Packet(Packets.CmdJoinArea)]
        public static void Handle(Packet packet)
        {
            var joinAreaPacket = new JoinAreaPacket(packet);
            packet.Sender.Send(new JoinAreaAnswer()
            {
                AreaId = joinAreaPacket.AreaId,
                Result = 1,
            }.CreatePacket());

            var joiningUser = packet.Sender.User;
            var joiningCharacter = joiningUser != null ? joiningUser.ActiveCharacter : null;
            if (joiningCharacter == null)
                return;

            // Do not rely on each client discovering the other player's vehicle serial
            // from movement packets. Explicitly exchange PlayerInfo for players that are
            // already in the same city/channel so visibility is bidirectional regardless
            // of login order or who starts moving first.
            foreach (var other in GameServer.Instance.Server.GetClients())
            {
                if (other == null || other == packet.Sender || other.User == null || other.User.ActiveCharacter == null)
                    continue;

                var otherCharacter = other.User.ActiveCharacter;
                if (otherCharacter.City != joiningCharacter.City ||
                    otherCharacter.LastChannel != joiningCharacter.LastChannel)
                    continue;

                if (other.User.VehicleSerial == 0 || joiningUser.VehicleSerial == 0)
                    continue;

                try
                {
                    packet.Sender.Send(new PlayerInfoOldAnswer
                    {
                        PlayerInfo = new XiPlayerInfo(other.User.VehicleSerial, otherCharacter)
                    }.CreatePacket());

                    other.Send(new PlayerInfoOldAnswer
                    {
                        PlayerInfo = new XiPlayerInfo(joiningUser.VehicleSerial, joiningCharacter)
                    }.CreatePacket());

                    Log.Debug("JoinArea player sync: {0}(serial={1}) <-> {2}(serial={3}) city={4} channel={5}",
                        joiningCharacter.Name, joiningUser.VehicleSerial,
                        otherCharacter.Name, other.User.VehicleSerial,
                        joiningCharacter.City, joiningCharacter.LastChannel);
                }
                catch (Exception ex)
                {
                    Log.Warning("JoinArea player sync failed for {0} <-> {1}: {2}",
                        joiningCharacter.Name, otherCharacter.Name, ex.Message);
                }
            }
        }
    }
}
