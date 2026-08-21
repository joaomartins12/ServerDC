using GameServer.Util;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class VisualItemList
    {
        [Packet(Packets.CmdVisualItemList)]
        public static void Handle(Packet packet)
        {
            SendCurrent(packet);
        }

        /// <summary>
        /// Rebuilds the Visual Shop inventory from persistent server state.
        /// 1201 is the authoritative inventory/equipped snapshot; 1061 restores the
        /// active car's persisted paint/tint once JoinChannel has assigned this process'
        /// live serial. A VehicleSerial read from the account row before JoinChannel is
        /// only the previous session's persisted value and must never target 1061.
        /// </summary>
        public static void SendCurrent(Packet packet)
        {
            var user = packet.Sender.User;
            var character = user == null ? null : user.ActiveCharacter;
            if (character == null)
            {
                packet.Sender.Send(new VisualItemListAnswer().CreatePacket());
                return;
            }

            var answer = BuildAnswer(character);
            Log.Debug("VisualItemListAck authoritative: CID={0} Count={1}", character.Id, answer.Items.Count);
            packet.Sender.Send(answer.CreatePacket());
            SendInitialLocalVisualRefresh(packet, user, character);
        }

        public static VisualItemListAnswer BuildAnswer(Character character)
        {
            var answer = new VisualItemListAnswer();
            if (character == null) return answer;

            using (var conn = GameServer.Instance.Database.Connection)
            {
                var rows = VisualShopDatabase.LoadInventory(conn, character.Id);
                foreach (var row in rows)
                {
                    answer.Items.Add(new InventoryVisualItem
                    {
                        CarId = row.CarId,
                        ItemState = row.ItemState,
                        TableIdx = row.ShopId,
                        InvenIdx = row.InventoryIndex,
                        PlateName = row.Data ?? string.Empty,
                        Period = row.Period,
                        UpdateTime = unchecked((int)row.UpdateTime),
                        CreateTime = unchecked((int)row.CreateTime)
                    });
                }
            }
            return answer;
        }

        private static void SendInitialLocalVisualRefresh(Packet packet, User user, Character character)
        {
            if (packet == null || packet.Sender == null || user == null || character == null ||
                character.ActiveCar == null || user.VehicleSerial == 0)
                return;

            User liveOwner;
            if (!DefaultServer.ActiveSerials.TryGetValue(user.VehicleSerial, out liveOwner) ||
                !ReferenceEquals(liveOwner, user))
            {
                Log.Debug(
                    "Visual inventory refresh deferred: CID={0} PersistedSerial={1} has not been assigned by JoinChannel yet; 1201 sent, 1061 skipped.",
                    character.Id, user.VehicleSerial);
                return;
            }

            SendLocalVisualUpdate(packet.Sender, user, character, "inventory");
        }

        public static void SendLocalVisualUpdate(Client recipient, User user, Character character, string reason)
        {
            if (recipient == null || user == null || character == null || character.ActiveCar == null ||
                user.VehicleSerial == 0)
                return;

            User liveOwner;
            if (!DefaultServer.ActiveSerials.TryGetValue(user.VehicleSerial, out liveOwner) ||
                !ReferenceEquals(liveOwner, user))
                return;

            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);
            var vehicle = character.ActiveCar;
            var effectiveColor = vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor;
            recipient.Send(new VisualUpdateAnswer
            {
                Serial = user.VehicleSerial,
                Age = 0,
                CarId = vehicle.CarId,
                VisualState = 1,
                CarInfo = new XiStrCarInfo
                {
                    CarID = vehicle.CarId,
                    CarType = vehicle.CarType,
                    BaseColor = effectiveColor,
                    Grade = vehicle.Grade,
                    SlotType = vehicle.SlotType,
                    AuctionCnt = vehicle.AuctionCnt,
                    Mitron = vehicle.Mitron,
                    Kmh = vehicle.Kmh,
                    Color = effectiveColor,
                    Color2 = vehicle.Color2,
                    MitronCapacity = vehicle.MitronCapacity,
                    MitronEfficiency = vehicle.MitronEfficiency,
                    AuctionOn = vehicle.AuctionOn,
                    SBBOn = vehicle.SBBOn
                }
            }.CreatePacket());

            Log.Debug(
                "Local visual update[{0}]: CID={1} Serial={2} CarId={3} Color=0x{4:X6} Color2=0x{5:X8} -> 1061",
                reason ?? string.Empty, character.Id, user.VehicleSerial, vehicle.CarId,
                effectiveColor, vehicle.Color2);
        }
    }
}
