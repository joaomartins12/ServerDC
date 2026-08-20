using System;
using System.Collections.Generic;
using System.IO;
using Shared.Network;

namespace AreaServer.Network.Handlers
{
    public class MoveVehicle
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<ushort, byte[]> LastMovement = new Dictionary<ushort, byte[]>();
        private static readonly Dictionary<ushort, int> SerialArea = new Dictionary<ushort, int>();

        public static void RegisterArea(ushort serial, int areaId)
        {
            if (serial == 0) return;
            lock (Sync)
                SerialArea[serial] = areaId;
        }

        public static void ReplayExisting(Client client, ushort ownSerial, int areaId)
        {
            if (client == null) return;

            List<KeyValuePair<ushort, byte[]>> snapshot;
            lock (Sync)
                snapshot = new List<KeyValuePair<ushort, byte[]>>(LastMovement);

            foreach (var pair in snapshot)
            {
                if (pair.Key == ownSerial) continue;

                int playerArea;
                lock (Sync)
                {
                    if (!SerialArea.TryGetValue(pair.Key, out playerArea) || playerArea != areaId)
                        continue;
                }

                SendMovement(client, pair.Key, pair.Value);
                WritePresenceLog("REPLAY", pair.Key, ownSerial, areaId, pair.Value.Length);
            }
        }

        public static void AnnounceCurrentToArea(Client enteringClient, ushort serial, int areaId)
        {
            byte[] movement;
            lock (Sync)
            {
                if (!LastMovement.TryGetValue(serial, out movement))
                    return;
            }

            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (client == null || client == enteringClient || client.User == null)
                    continue;

                var targetSerial = client.User.VehicleSerial;
                int targetArea;
                lock (Sync)
                {
                    if (!SerialArea.TryGetValue(targetSerial, out targetArea) || targetArea != areaId)
                        continue;
                }

                SendMovement(client, serial, movement);
                WritePresenceLog("ANNOUNCE", serial, targetSerial, areaId, movement.Length);
            }
        }

        [Packet(Packets.CmdMoveVehicle)]
        public static void Handle(Packet packet)
        {
            if (packet.Sender == null || packet.Sender.User == null)
                return;

            var packetSerial = packet.Reader.ReadUInt16();
            var vehicleSerial = packet.Sender.User.VehicleSerial;
            if (vehicleSerial == 0)
                return;

            if (packetSerial != vehicleSerial)
                WritePresenceLog("SERIAL_MISMATCH", packetSerial, vehicleSerial, -1, 0);

            var stream = packet.Reader.BaseStream;
            var remaining = (int)Math.Max(0, stream.Length - stream.Position);
            var movement = packet.Reader.ReadBytes(remaining);

            int areaId;
            lock (Sync)
            {
                LastMovement[vehicleSerial] = movement;
                if (!SerialArea.TryGetValue(vehicleSerial, out areaId))
                {
                    WritePresenceLog("NO_AREA", vehicleSerial, 0, -1, movement.Length);
                    return;
                }
            }

            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (client == null || client == packet.Sender || client.User == null)
                    continue;

                var targetSerial = client.User.VehicleSerial;
                int targetArea;
                lock (Sync)
                {
                    if (!SerialArea.TryGetValue(targetSerial, out targetArea) || targetArea != areaId)
                        continue;
                }

                // This build uses packet 541 for both remote-vehicle discovery and live
                // movement. Packet 542 makes an already visible remote vehicle disappear.
                // Relay exactly one fresh 541 per incoming movement; never replay cached
                // movement here, which previously doubled traffic and caused rubber-banding.
                SendMovement(client, vehicleSerial, movement);
                WritePresenceLog("LIVE", vehicleSerial, targetSerial, areaId, movement.Length);
            }
        }

        private static void SendMovement(Client client, ushort serial, byte[] movement)
        {
            var move = new Packet(Packets.CmdMoveVehicle);
            move.Writer.Write(serial);
            move.Writer.Write(movement ?? new byte[0]);
            client.Send(move);
        }

        private static void WritePresenceLog(string action, ushort sourceSerial, ushort targetSerial, int areaId, int bodyLength)
        {
            try
            {
                var dir = Path.Combine("Logs", "Research");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "PresenceSync.txt"),
                    string.Format("{0:O} {1} source={2} target={3} area={4} body={5}{6}",
                        DateTime.UtcNow, action, sourceSerial, targetSerial, areaId, bodyLength, Environment.NewLine));
            }
            catch
            {
            }
        }
    }
}
