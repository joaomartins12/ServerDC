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
        // after JoinArea. One fixed replay at 250ms can still race that state on a busy
        // machine or during an area TCP handover. Retry only during the first few seconds:
        // 250ms gets the fast path, 1250ms is after the normal GameServer delayed resync,
        // and 3000ms is a final recovery pass. Normal 541 movement remains authoritative.
        private static readonly int[] InitialPresenceReplayDelaysMs = { 250, 1250, 3000 };

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
            // the serial to the newest authenticated User object. Client.KillConnection()
            // now also verifies this ownership before it is allowed to broadcast packet 550.
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
                var elapsedDelay = 0;

                for (var attempt = 0; attempt < InitialPresenceReplayDelaysMs.Length; attempt++)
                {
                    var targetDelay = InitialPresenceReplayDelaysMs[attempt];
                    var sleep = targetDelay - elapsedDelay;
                    if (sleep > 0)
                        Thread.Sleep(sleep);
                    elapsedDelay = targetDelay;

                    try
                    {
                        if (client == null || client.User == null || client.User.VehicleSerial != serial)
                            return;

                        Shared.Objects.User active;
                        if (!DefaultServer.ActiveSerials.TryGetValue(serial, out active) ||
                            !ReferenceEquals(active, client.User))
                            return;

                        // Replay other already-moving players to the entrant and announce
                        // the entrant's latest movement back to current players. Repeating
                        // this only during startup makes the creation/identity ordering
                        // deterministic without adding a permanent movement heartbeat.
                        MoveVehicle.ReplayExisting(client, serial, areaId);
                        MoveVehicle.AnnounceCurrentToArea(client, serial, areaId);

                        if (attempt == InitialPresenceReplayDelaysMs.Length - 1)
                        {
                            Log.Debug(
                                "Area initial presence sync complete: Serial={0} Area={1} Attempts={2} WindowMs={3}",
                                serial,
                                areaId,
                                InitialPresenceReplayDelaysMs.Length,
                                targetDelay);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(
                            "Area initial presence replay failed: Serial={0} Area={1} Attempt={2} Error={3}",
                            serial,
                            areaId,
                            attempt + 1,
                            ex.Message);
                    }
                }
            });
        }
    }
}
