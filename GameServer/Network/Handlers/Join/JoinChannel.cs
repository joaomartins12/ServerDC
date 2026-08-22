using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
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
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null)
            {
                packet.Sender.SendError("no_active_character");
                return;
            }

            var serial = AllocateSerial();
            DefaultServer.ActiveSerials[serial] = packet.Sender.User;
            packet.Sender.User.VehicleSerial = serial;

            if (!AccountModel.UpdateVehicleSerial(GameServer.Instance.Database.Connection, packet.Sender.User.Id, serial))
            {
                packet.Sender.KillConnection("Failed to update serial.");
                return;
            }

            // Retail starts every world/channel incarnation with a non-zero generation.
            // Appearance changes advance the same value; stale packets from an earlier
            // incarnation therefore cannot cull the current player object.
            var sessionAge = WorldSessionAge.Begin(character.Id);

            packet.Sender.Send(new JoinChannelAnswer()
            {
                ChannelName = "speeding",
                CharacterName = character.Name,
                Serial = (short)serial,
                SessionAge = sessionAge,
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
            if (character.ActiveCar != null)
            {
                PlayerVisualSnapshotBuilder.ApplyActivePaint(character);
                VisualItemList.SendLocalVisualUpdate(packet.Sender, packet.Sender.User, character, "join-channel");
                var visualInventory = VisualItemList.BuildAnswer(character);
                packet.Sender.Send(visualInventory.CreatePacket());
                Log.Debug(
                    "JoinChannel visual bootstrap: CID={0} Serial={1} Age={2} CarId={3} Count={4} -> 1061+1201",
                    character.Id, serial, sessionAge, character.ActiveCar.CarId, visualInventory.Items.Count);
            }

            BonusUpdateService.SendCurrent(packet.Sender, "join-channel");
            packet.Sender.SendChatMessage($"Server powered by DCNC (v{Shared.Util.Version.GetVersion()}) - GigaToni");
        }
    }
}
