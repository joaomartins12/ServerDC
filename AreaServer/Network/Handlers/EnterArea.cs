using Shared.Models;
using Shared.Network;
using Shared.Network.AreaServer;
using Shared.Util;

namespace AreaServer.Network.Handlers
{
    public static class EnterArea
    {
        [Packet(Packets.CmdEnterArea)]
        public static void Handle(Packet packet)
        {
            var enterAreaPacket = new EnterAreaPacket(packet);

            if (packet.Sender.User == null || packet.Sender.User.VehicleSerial != enterAreaPacket.VehicleSerial)
            {
                var character = CharacterModel.Retrieve(AreaServer.Instance.Database.Connection, enterAreaPacket.CharacterName);
                if (character == null)
                {
                    packet.Sender.KillConnection("Invalid charactername");
                    return;
                }

                var account = AccountModel.RetrieveFromSerial(
                    AreaServer.Instance.Database.Connection,
                    character.Uid,
                    enterAreaPacket.VehicleSerial);
                if (account == null)
                {
                    packet.Sender.KillConnection("Invalid serial");
                    return;
                }

                packet.Sender.User = account;
                packet.Sender.User.ActiveCharacter = character;
            }

            if (packet.Sender.User.VehicleSerial != enterAreaPacket.VehicleSerial)
            {
                packet.Sender.KillConnection(
                    $"[{packet.Sender.User.VehicleSerial} vs {enterAreaPacket.VehicleSerial}] Still wrong user.");
                return;
            }

            // Area transitions can create a fresh TCP connection while the previous
            // connection with the same vehicle serial is still shutting down. Always bind
            // the serial to the newest authenticated User object. Otherwise the old
            // Client.KillConnection() may remove the serial later and make the new live
            // session look inactive to presence/movement routing.
            Shared.Objects.User previousOwner = null;
            DefaultServer.ActiveSerials.TryGetValue(enterAreaPacket.VehicleSerial, out previousOwner);
            DefaultServer.ActiveSerials[enterAreaPacket.VehicleSerial] = packet.Sender.User;

            if (previousOwner != null && !ReferenceEquals(previousOwner, packet.Sender.User))
            {
                Log.Debug(
                    "Area serial ownership rebound: Serial={0} Character={1} Area={2}",
                    enterAreaPacket.VehicleSerial,
                    enterAreaPacket.CharacterName,
                    enterAreaPacket.AreaId);
            }

            MoveVehicle.RegisterArea(packet.Sender, enterAreaPacket.VehicleSerial, enterAreaPacket.AreaId);

            packet.Sender.Send(new EnterAreaAnswer
            {
                LocalTime = enterAreaPacket.LocalTime,
                AreaId = enterAreaPacket.AreaId
            }.CreatePacket());

            // Refresh both directions. The entering client discovers drivers already in
            // this map, while drivers already present are reminded of this serial when a
            // cached movement exists (important after Dealership/Garage/Shop transitions).
            MoveVehicle.ReplayExisting(packet.Sender, enterAreaPacket.VehicleSerial, enterAreaPacket.AreaId);
            MoveVehicle.AnnounceCurrentToArea(packet.Sender, enterAreaPacket.VehicleSerial, enterAreaPacket.AreaId);
        }
    }
}
