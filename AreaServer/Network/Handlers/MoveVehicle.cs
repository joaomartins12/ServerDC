using System.Collections.Generic;
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
            {
                snapshot = new List<KeyValuePair<ushort, byte[]>>(LastMovement);
            }

            foreach (var pair in snapshot)
            {
                if (pair.Key == ownSerial) continue;

                int playerArea;
                lock (Sync)
                {
                    if (!SerialArea.TryGetValue(pair.Key, out playerArea) || playerArea != areaId)
                        continue;
                }

                var replay = new Packet(Packets.CmdMoveVehicle);
                replay.Writer.Write(pair.Key);
                replay.Writer.Write(pair.Value);
                client.Send(replay);
            }
        }

        [Packet(Packets.CmdMoveVehicle)]
        public static void Handle(Packet packet)
        {
            var vehicleSerial = packet.Reader.ReadUInt16();
            var movement = packet.Reader.ReadBytes(112);

            lock (Sync)
            {
                LastMovement[vehicleSerial] = movement;
            }

            var move = new Packet(Packets.CmdMoveVehicle);
            move.Writer.Write(vehicleSerial);
            move.Writer.Write(movement);

            AreaServer.Instance.Server.Broadcast(move, packet.Sender);
        }
    }
}
