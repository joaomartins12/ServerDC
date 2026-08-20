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

                // This client discovers remote vehicles from the same movement packet id
                // it emits (541). Replaying 542 prevents remote-player discovery.
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

            // Movement bodies are not always the same size in this client (the captured
            // wire packets vary). Preserve every remaining byte instead of truncating or
            // padding to a guessed 112-byte structure.
            var remaining = (int)(packet.Reader.BaseStream.Length - packet.Reader.BaseStream.Position);
            var movement = remaining > 0 ? packet.Reader.ReadBytes(remaining) : new byte[0];

            lock (Sync)
                LastMovement[vehicleSerial] = movement;

            var move = new Packet(Packets.CmdMoveVehicle);
            move.Writer.Write(vehicleSerial);
            move.Writer.Write(movement);

            AreaServer.Instance.Server.Broadcast(move, packet.Sender);
        }
    }
}
