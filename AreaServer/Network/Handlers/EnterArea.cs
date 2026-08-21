using System;
using System.Threading;
using Shared.Models;
using Shared.Network;
using Shared.Network.AreaServer;
using Shared.Util;

namespace AreaServer.Network.Handlers
{
    public static class EnterArea
    {
        // GameServer publishes the 802/809 XiPlayerInfo identity/visual snapshot just
        // after JoinArea. If AreaServer replays a cached 541 immediately, v0.77a creates
        // the remote vehicle before that visual identity exists and the car remains in
        // its default appearance. A short one-shot delay preserves the normal movement
        // stream while allowing the retail player-info manager to be populated first.
        private const int InitialPresenceReplayDelayMs = 250;

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

            QueueInitialPresenceReplay(
                packet.Sender,
                enterAreaPacket.VehicleSerial,
                enterAreaPacket.AreaId);
        }

        private static void QueueInitialPresenceReplay(Client client, ushort serial, int areaId)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(InitialPresenceReplayDelayMs);

                try
                {
                    if (client == null || client.User == null || client.User.VehicleSerial != serial)
                        return;

                    Shared.Objects.User active;
                    if (!DefaultServer.ActiveSerials.TryGetValue(serial, out active) ||
                        !ReferenceEquals(active, client.User))
                        return;

                    // By now the GameServer has normally delivered 802 + 809 + 806 for
                    // the players in this area. The first relayed 541 can therefore bind
                    // the world vehicle to an already-populated XiPlayerInfo/XiVisualItem.
                    MoveVehicle.ReplayExisting(client, serial, areaId);
                    MoveVehicle.AnnounceCurrentToArea(client, serial, areaId);

                    Log.Debug(
                        "Area initial presence replay: Serial={0} Area={1} DelayMs={2}",
                        serial,
                        areaId,
                        InitialPresenceReplayDelayMs);
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        "Area initial presence replay failed: Serial={0} Area={1} Error={2}",
                        serial,
                        areaId,
                        ex.Message);
                }
            });
        }
    }
}
