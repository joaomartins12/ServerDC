using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shared.Models;
using Shared.Network;
using Shared.Objects;

namespace AreaServer.Network.Handlers
{
    public class MoveVehicle
    {
        private sealed class PresenceState
        {
            public string Name = string.Empty;
            public int AreaId = -1;
            public DateTime LastEnterUtc;
            public DateTime LastReceiveUtc;
            public DateTime LastRelayUtc;
            public long ReceiveCount;
            public long RelayCount;
            public int LastBodyLength;
        }

        private sealed class VisualAttrState
        {
            public uint CarId;
            public ushort Body;
            public uint Color;
            public uint Color2;
            public DateTime RefreshedUtc;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<ushort, byte[]> LastMovement = new Dictionary<ushort, byte[]>();
        private static readonly Dictionary<ushort, int> SerialArea = new Dictionary<ushort, int>();
        private static readonly Dictionary<ushort, PresenceState> Presence = new Dictionary<ushort, PresenceState>();
        private static readonly Dictionary<ulong, long> PairRelayCounts = new Dictionary<ulong, long>();
        private static readonly Dictionary<ushort, VisualAttrState> VisualAttrs = new Dictionary<ushort, VisualAttrState>();
        private static readonly TimeSpan VisualAttrRefreshInterval = TimeSpan.FromMilliseconds(500);
        private static DateTime _lastSnapshotUtc = DateTime.MinValue;

        public static void RegisterArea(ushort serial, int areaId)
        {
            RegisterArea(null, serial, areaId);
        }

        public static void RegisterArea(Client client, ushort serial, int areaId)
        {
            if (serial == 0) return;

            var now = DateTime.UtcNow;
            var name = client?.User?.ActiveCharacter?.Name ?? string.Empty;
            var previousArea = -1;
            lock (Sync)
            {
                SerialArea.TryGetValue(serial, out previousArea);
                SerialArea[serial] = areaId;

                PresenceState state;
                if (!Presence.TryGetValue(serial, out state))
                    Presence[serial] = state = new PresenceState();

                if (!string.IsNullOrWhiteSpace(name)) state.Name = name;
                state.AreaId = areaId;
                state.LastEnterUtc = now;
            }

            WritePresenceLog(previousArea == areaId ? "ENTER_REFRESH" : "ENTER_AREA",
                serial, 0, areaId, 0,
                "name=" + Safe(name) + " previousArea=" + previousArea);
            MaybeWriteSnapshot(true);
        }

        /// <summary>
        /// Builds the 100-entry area population table expected by AreaStatusAck.
        /// Only the User object currently owning a serial in ActiveSerials is counted.
        /// This prevents obsolete AreaServer TCP sessions from inflating presence.
        /// </summary>
        public static uint[] GetAreaUserCounts()
        {
            var counts = new uint[100];
            Dictionary<ushort, int> areaSnapshot;
            lock (Sync)
                areaSnapshot = new Dictionary<ushort, int>(SerialArea);

            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (!IsCurrentSerialOwner(client))
                    continue;

                var serial = client.User.VehicleSerial;
                int areaId;
                if (!areaSnapshot.TryGetValue(serial, out areaId))
                    continue;
                if (areaId < 0 || areaId >= counts.Length)
                    continue;
                counts[areaId]++;
            }

            MaybeWriteSnapshot(false);
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

                if (!IsSerialActive(pair.Key))
                {
                    WritePresenceLog("REPLAY_SKIP_INACTIVE", pair.Key, ownSerial, areaId, pair.Value == null ? 0 : pair.Value.Length, null);
                    continue;
                }

                SendMovement(client, pair.Key, pair.Value);
                RecordRelay(pair.Key, ownSerial);
                WritePresenceLog("REPLAY", pair.Key, ownSerial, areaId, pair.Value == null ? 0 : pair.Value.Length, null);
            }
        }

        public static void AnnounceCurrentToArea(Client enteringClient, ushort serial, int areaId)
        {
            byte[] movement;
            Dictionary<ushort, int> areaSnapshot;
            lock (Sync)
            {
                if (!LastMovement.TryGetValue(serial, out movement))
                {
                    WritePresenceLog("ANNOUNCE_NO_CACHE", serial, 0, areaId, 0, null);
                    return;
                }
                areaSnapshot = new Dictionary<ushort, int>(SerialArea);
            }

            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (client == null || client == enteringClient || !IsCurrentSerialOwner(client))
                    continue;

                var targetSerial = client.User.VehicleSerial;
                int targetArea;
                if (!areaSnapshot.TryGetValue(targetSerial, out targetArea) || targetArea != areaId)
                    continue;

                SendMovement(client, serial, movement);
                RecordRelay(serial, targetSerial);
                WritePresenceLog("ANNOUNCE", serial, targetSerial, areaId, movement == null ? 0 : movement.Length, null);
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
            {
                WritePresenceLog("MOVE_ZERO_SERIAL", packetSerial, 0, -1, 0, null);
                return;
            }

            if (packetSerial != vehicleSerial)
                WritePresenceLog("SERIAL_MISMATCH", packetSerial, vehicleSerial, -1, 0,
                    "name=" + Safe(packet.Sender.User.ActiveCharacter?.Name));

            var stream = packet.Reader.BaseStream;
            var remaining = (int)Math.Max(0, stream.Length - stream.Position);
            var movement = packet.Reader.ReadBytes(remaining);

            // DriftCity v0.77a constructs remote world cars directly from the XiCarAttr
            // embedded in packet 541. The retail client leaves Color/Color2 at zero in
            // its outgoing movement stream, so blindly relaying that stream creates a
            // default-looking vehicle on every other client. Patch only the XiCarAttr
            // fields from the server-authoritative vehicle row before caching/relaying.
            PatchAuthoritativeCarAttr(packet.Sender, vehicleSerial, movement);

            int areaId;
            Dictionary<ushort, int> areaSnapshot;
            var now = DateTime.UtcNow;
            double previousAgeSeconds = 0;
            lock (Sync)
            {
                LastMovement[vehicleSerial] = movement;
                if (!SerialArea.TryGetValue(vehicleSerial, out areaId))
                {
                    WritePresenceLog("NO_AREA", vehicleSerial, 0, -1, movement.Length,
                        "name=" + Safe(packet.Sender.User.ActiveCharacter?.Name));
                    return;
                }

                PresenceState state;
                if (!Presence.TryGetValue(vehicleSerial, out state))
                    Presence[vehicleSerial] = state = new PresenceState();

                if (state.LastReceiveUtc != DateTime.MinValue)
                    previousAgeSeconds = (now - state.LastReceiveUtc).TotalSeconds;
                state.Name = packet.Sender.User.ActiveCharacter?.Name ?? state.Name;
                state.AreaId = areaId;
                state.LastReceiveUtc = now;
                state.ReceiveCount++;
                state.LastBodyLength = movement.Length;
                areaSnapshot = new Dictionary<ushort, int>(SerialArea);
            }

            if (previousAgeSeconds >= 10.0)
                WritePresenceLog("MOVE_RESUMED_AFTER_GAP", vehicleSerial, 0, areaId, movement.Length,
                    "gapSeconds=" + previousAgeSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));

            var recipients = 0;
            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (client == null || client == packet.Sender || !IsCurrentSerialOwner(client))
                    continue;

                var targetSerial = client.User.VehicleSerial;
                int targetArea;
                if (!areaSnapshot.TryGetValue(targetSerial, out targetArea) || targetArea != areaId)
                    continue;

                // v0.77 uses packet 541 for discovery and live movement. Exactly one
                // fresh relay is emitted to every current serial owner in this area.
                SendMovement(client, vehicleSerial, movement);
                RecordRelay(vehicleSerial, targetSerial);
                recipients++;
            }

            if (recipients == 0 && CountCurrentPlayersInArea(areaId, vehicleSerial) > 0)
                WritePresenceLog("MOVE_NO_RECIPIENT", vehicleSerial, 0, areaId, movement.Length,
                    "otherCurrentPlayersDetected=true");

            MaybeWriteSnapshot(false);
        }

        private static void PatchAuthoritativeCarAttr(Client source, ushort serial, byte[] movement)
        {
            // movement begins immediately after VehicleSerial because Handle consumed
            // that WORD already. Layout from the v0.77a client handler:
            // +00 Age, +02 Sort, +04 Body, +06 Color, +0A Color2, +0E State.
            if (source?.User?.ActiveCharacter == null || movement == null || movement.Length < 18)
                return;

            var activeCarId = source.User.ActiveCharacter.ActiveVehicleId;
            if (activeCarId == 0) return;

            var now = DateTime.UtcNow;
            VisualAttrState state;
            lock (Sync)
            {
                if (VisualAttrs.TryGetValue(serial, out state) &&
                    state.CarId == activeCarId &&
                    now - state.RefreshedUtc < VisualAttrRefreshInterval)
                {
                    WriteCarAttr(movement, state);
                    return;
                }
            }

            try
            {
                Vehicle vehicle;
                using (var conn = AreaServer.Instance.Database.Connection)
                    vehicle = VehicleModel.Retrieve(conn, activeCarId);

                if (vehicle == null) return;

                var effectiveColor = vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor;
                var refreshed = new VisualAttrState
                {
                    CarId = activeCarId,
                    Body = unchecked((ushort)vehicle.CarType),
                    Color = effectiveColor,
                    Color2 = vehicle.Color2,
                    RefreshedUtc = now
                };

                var changed = state == null || state.CarId != refreshed.CarId || state.Body != refreshed.Body ||
                              state.Color != refreshed.Color || state.Color2 != refreshed.Color2;

                lock (Sync)
                    VisualAttrs[serial] = refreshed;

                WriteCarAttr(movement, refreshed);

                if (changed)
                {
                    Log.Info(
                        "Area authoritative 541 visual: Name={0} Serial={1} CarId={2} Body={3} Color=0x{4:X6} Color2=0x{5:X8}",
                        source.User.ActiveCharacter.Name,
                        serial,
                        refreshed.CarId,
                        refreshed.Body,
                        refreshed.Color,
                        refreshed.Color2);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Area authoritative 541 visual lookup failed: Serial={0} CarId={1} Error={2}",
                    serial, activeCarId, ex.Message);
            }
        }

        private static void WriteCarAttr(byte[] movement, VisualAttrState state)
        {
            if (movement == null || movement.Length < 18 || state == null) return;

            // Player-car sort is zero. Preserve State because its live semantics are
            // movement/client-owned, while Body and both colors are authoritative data.
            movement[2] = 0;
            movement[3] = 0;
            Buffer.BlockCopy(BitConverter.GetBytes(state.Body), 0, movement, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(state.Color), 0, movement, 6, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(state.Color2), 0, movement, 10, 4);
        }

        private static void RecordRelay(ushort sourceSerial, ushort targetSerial)
        {
            lock (Sync)
            {
                PresenceState state;
                if (!Presence.TryGetValue(sourceSerial, out state))
                    Presence[sourceSerial] = state = new PresenceState();
                state.LastRelayUtc = DateTime.UtcNow;
                state.RelayCount++;

                var key = PairKey(sourceSerial, targetSerial);
                long count;
                PairRelayCounts.TryGetValue(key, out count);
                PairRelayCounts[key] = count + 1;
            }
        }

        private static int CountCurrentPlayersInArea(int areaId, ushort excludeSerial)
        {
            var count = 0;
            foreach (var client in AreaServer.Instance.Server.GetClients())
            {
                if (!IsCurrentSerialOwner(client)) continue;
                var serial = client.User.VehicleSerial;
                if (serial == excludeSerial) continue;
                int candidateArea;
                lock (Sync)
                {
                    if (!SerialArea.TryGetValue(serial, out candidateArea)) continue;
                }
                if (candidateArea == areaId) count++;
            }
            return count;
        }

        private static bool IsCurrentSerialOwner(Client client)
        {
            if (client == null || client.User == null || client.User.VehicleSerial == 0)
                return false;

            try
            {
                User active;
                return DefaultServer.ActiveSerials.TryGetValue(client.User.VehicleSerial, out active) &&
                       ReferenceEquals(active, client.User);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSerialActive(ushort serial)
        {
            if (serial == 0) return false;
            try
            {
                return DefaultServer.ActiveSerials.ContainsKey(serial);
            }
            catch
            {
                return false;
            }
        }

        private static void SendMovement(Client client, ushort serial, byte[] movement)
        {
            var move = new Packet(Packets.CmdMoveVehicle);
            move.Writer.Write(serial);
            move.Writer.Write(movement ?? new byte[0]);
            client.Send(move);
        }

        /// <summary>
        /// Emits one aggregate snapshot every five seconds instead of one disk write per
        /// movement frame. This makes disappearance bugs diagnosable without adding sync lag.
        /// </summary>
        private static void MaybeWriteSnapshot(bool force)
        {
            var now = DateTime.UtcNow;
            Dictionary<ushort, PresenceState> states;
            Dictionary<ushort, int> areas;
            Dictionary<ulong, long> pairs;

            lock (Sync)
            {
                if (!force && (now - _lastSnapshotUtc).TotalSeconds < 5.0)
                    return;
                _lastSnapshotUtc = now;

                PruneInactiveUnsafe();
                states = new Dictionary<ushort, PresenceState>();
                foreach (var pair in Presence)
                {
                    var source = pair.Value;
                    states[pair.Key] = new PresenceState
                    {
                        Name = source.Name,
                        AreaId = source.AreaId,
                        LastEnterUtc = source.LastEnterUtc,
                        LastReceiveUtc = source.LastReceiveUtc,
                        LastRelayUtc = source.LastRelayUtc,
                        ReceiveCount = source.ReceiveCount,
                        RelayCount = source.RelayCount,
                        LastBodyLength = source.LastBodyLength
                    };
                }
                areas = new Dictionary<ushort, int>(SerialArea);
                pairs = new Dictionary<ulong, long>(PairRelayCounts);
            }

            try
            {
                var sb = new StringBuilder();
                sb.Append(now.ToString("O")).Append(" SNAPSHOT activeSerials=");
                var first = true;
                foreach (var pair in states)
                {
                    if (!first) sb.Append(" ; ");
                    first = false;
                    var state = pair.Value;
                    var rxAge = state.LastReceiveUtc == DateTime.MinValue ? -1 : (now - state.LastReceiveUtc).TotalSeconds;
                    var txAge = state.LastRelayUtc == DateTime.MinValue ? -1 : (now - state.LastRelayUtc).TotalSeconds;
                    sb.Append("serial=").Append(pair.Key)
                      .Append(",name=").Append(Safe(state.Name))
                      .Append(",area=").Append(state.AreaId)
                      .Append(",rx=").Append(state.ReceiveCount)
                      .Append(",tx=").Append(state.RelayCount)
                      .Append(",rxAge=").Append(rxAge.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",txAge=").Append(txAge.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",body=").Append(state.LastBodyLength);
                }

                sb.Append(" pairs=");
                first = true;
                foreach (var pair in pairs)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append((ushort)(pair.Key >> 16)).Append("->").Append((ushort)pair.Key).Append('=').Append(pair.Value);
                }
                AppendPresenceLine(sb.ToString());
            }
            catch
            {
            }
        }

        private static void PruneInactiveUnsafe()
        {
            var inactive = new List<ushort>();
            foreach (var serial in SerialArea.Keys)
            {
                if (!IsSerialActive(serial)) inactive.Add(serial);
            }

            foreach (var serial in inactive)
            {
                SerialArea.Remove(serial);
                LastMovement.Remove(serial);
                Presence.Remove(serial);
                VisualAttrs.Remove(serial);
            }

            if (inactive.Count != 0)
            {
                var removePairs = new List<ulong>();
                foreach (var key in PairRelayCounts.Keys)
                {
                    var source = (ushort)(key >> 16);
                    var target = (ushort)key;
                    if (inactive.Contains(source) || inactive.Contains(target)) removePairs.Add(key);
                }
                foreach (var key in removePairs) PairRelayCounts.Remove(key);
            }
        }

        private static ulong PairKey(ushort source, ushort target)
        {
            return ((ulong)source << 16) | target;
        }

        private static void WritePresenceLog(string action, ushort sourceSerial, ushort targetSerial,
            int areaId, int bodyLength, string detail)
        {
            var line = string.Format("{0:O} {1} source={2} target={3} area={4} body={5}{6}",
                DateTime.UtcNow, action, sourceSerial, targetSerial, areaId, bodyLength,
                string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
            AppendPresenceLine(line);
        }

        private static void AppendPresenceLine(string line)
        {
            try
            {
                var dir = Path.Combine("Logs", "Research");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "PresenceSync.txt"), line + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "?" : value.Replace(" ", "_");
        }
    }
}
