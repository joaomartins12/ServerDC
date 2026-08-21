using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

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

            // VisualItemList can be requested before JoinChannel. In that phase the
            // account object's VehicleSerial is only the persisted serial from the last
            // session, so its 1061 is deliberately suppressed. Now the serial is live:
            // install the persisted car colour/tint first, then make the retail 1201
            // handler rebuild the equipped XiVisualItem on top of that car state.
            var character = packet.Sender.User.ActiveCharacter;
            if (character != null && character.ActiveCar != null)
            {
                PlayerVisualSnapshotBuilder.ApplyActivePaint(character);
                VisualItemList.SendLocalVisualUpdate(packet.Sender, packet.Sender.User, character, "join-channel");
                var visualInventory = VisualItemList.BuildAnswer(character);
                packet.Sender.Send(visualInventory.CreatePacket());
                Log.Debug(
                    "JoinChannel visual bootstrap: CID={0} Serial={1} CarId={2} Count={3} -> 1061+1201",
                    character.Id, serial, character.ActiveCar.CarId, visualInventory.Items.Count);
            }

            packet.Sender.SendChatMessage($"Server powered by DCNC (v{Shared.Util.Version.GetVersion()}) - GigaToni");
        }
    }
}
