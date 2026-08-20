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

        /// <summary>
        /// Builds the 100-entry area population table expected by AreaStatusAck.
        /// Only currently connected AreaServer clients are counted, so stale cached
        /// movement/serial records cannot inflate the result.
        /// </summary>
        public static uint[] GetAreaUserCounts()
        {
            var counts = new uint[100];

            Dictionary<ushort, int> areaSnapshot;
            lock (Sync)
                areaSnapshot = new Dictionary<ushort, int>(SerialArea);

            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (client == null || client.User == null)
                    continue;

                var serial = client.User.VehicleSerial;
                if (serial == 0)
                    continue;

                int areaId;
                if (!areaSnapshot.TryGetValue(serial, out areaId))
                    continue;

                if (areaId < 0 || areaId >= counts.Length)
                    continue;

                counts[areaId]++;
            }

            return counts;
        }

        public static void ReplayExisting(Client client, ushort ownSerial, int areaId)
        {
            if (client == null) return;

            List<KeyValuePair<ushort, byte[]>> snapshot;
            Dictionary<ushort, int> areaSnapshot;
            lock (Sync)
            {
                snapshot = new List<KeyValuePair<ushort, byte[]>>(LastMovement);
                areaSnapshot = new Dictionary<ushort, int>(SerialArea);
            }

            foreach (var pair in snapshot)
            {
                if (pair.Key == ownSerial) continue;

                int playerArea;
                if (!areaSnapshot.TryGetValue(pair.Key, out playerArea) || playerArea != areaId)
                    continue;

                SendMovement(client, pair.Key, pair.Value);
                WritePresenceLog("REPLAY", pair.Key, ownSerial, areaId, pair.Value.Length);
            }
        }

        public static void AnnounceCurrentToArea(Client enteringClient, ushort serial, int areaId)
        {
            byte[] movement;
            Dictionary<ushort, int> areaSnapshot;
            lock (Sync)
            {
                if (!LastMovement.TryGetValue(serial, out movement))
                    return;

                areaSnapshot = new Dictionary<ushort, int>(SerialArea);
            }

            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (client == null || client == enteringClient || client.User == null)
                    continue;

                var targetSerial = client.User.VehicleSerial;
                int targetArea;
                if (!areaSnapshot.TryGetValue(targetSerial, out targetArea) || targetArea != areaId)
                    continue;

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
            Dictionary<ushort, int> areaSnapshot;
            lock (Sync)
            {
                LastMovement[vehicleSerial] = movement;
                if (!SerialArea.TryGetValue(vehicleSerial, out areaId))
                {
                    WritePresenceLog("NO_AREA", vehicleSerial, 0, -1, movement.Length);
                    return;
                }

                areaSnapshot = new Dictionary<ushort, int>(SerialArea);
            }

            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (client == null || client == packet.Sender || client.User == null)
                    continue;

                var targetSerial = client.User.VehicleSerial;
                int targetArea;
                if (!areaSnapshot.TryGetValue(targetSerial, out targetArea) || targetArea != areaId)
                    continue;

                // This v0.77 build uses packet 541 for both discovery and live movement.
                // Relay exactly one fresh packet per recipient. Do not replay cached
                // movement on the live path: that causes stale-state rubber-banding.
                SendMovement(client, vehicleSerial, movement);

                // Deliberately do not synchronously append a LIVE line to disk here.
                // Movement packets are high frequency; per-frame File.AppendAllText was
                // unnecessary I/O and could itself introduce jitter under multiple users.
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
