using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;

namespace GameServer.Network.Handlers.Join
{
    public class JoinChannel
    {
        private static ushort AllocateSerial()
        {
            // Serial 0 is not reliable for remote-player discovery in this client.
            // Keep advancing until we find a non-zero serial that is not active in
            // this GameServer process.
            for (var i = 0; i < ushort.MaxValue; i++)
            {
                GameServer.Instance.Server.LastSerial++;
                if (GameServer.Instance.Server.LastSerial == 0)
                    GameServer.Instance.Server.LastSerial = 1;

                var candidate = GameServer.Instance.Server.LastSerial;
                if (!DefaultServer.ActiveSerials.ContainsKey(candidate))
                    return candidate;
            }

            throw new System.InvalidOperationException("No free vehicle serial is available.");
        }

        [Packet(Packets.CmdJoinChannel)]
        public static void Handle(Packet packet)
        {
            var serial = AllocateSerial();
            DefaultServer.ActiveSerials[serial] = packet.Sender.User;
            packet.Sender.User.VehicleSerial = serial;

            if (!AccountModel.UpdateVehicleSerial(GameServer.Instance.Database.Connection, packet.Sender.User.Id, serial))
            {
                packet.Sender.KillConnection("Failed to update serial.");
                return;
            }

            packet.Sender.Send(new JoinChannelAnswer()
            {
                ChannelName = "speeding",
                CharacterName = packet.Sender.User.ActiveCharacter.Name,
                Serial = (short)serial,
                SessionAge = 0,
            }.CreatePacket());

            packet.Sender.Send(new WeatherAnswer()
            {
                CurrentWeather = WeatherAnswer.Weather.Rain
            }.CreatePacket());

            packet.Sender.SendChatMessage($"Server powered by DCNC (v{Shared.Util.Version.GetVersion()}) - GigaToni");
        }
    }
}
