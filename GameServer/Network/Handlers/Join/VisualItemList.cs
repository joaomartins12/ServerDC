using System.Collections.Generic;
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

        public static void SendCurrent(Packet packet)
        {
            var user = packet.Sender.User;
            var character = user == null ? null : user.ActiveCharacter;
            if (character == null)
            {
                packet.Sender.Send(new VisualItemListAnswer().CreatePacket());
                return;
            }

            var answer = new VisualItemListAnswer();
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

            Log.Debug("VisualItemListAck authoritative: CID={0} Count={1}", character.Id, answer.Items.Count);
            packet.Sender.Send(answer.CreatePacket());

            // 1201 is the authoritative inventory state. Reset the car-info snapshot from
            // persisted/equipped data and then replay ONLY ItemState=1 rows. Replaying every
            // owned row caused v0.77a to restore preview/unequipped cosmetics locally.
            SendInitialLocalVisualRefresh(packet, user, character);
            SendPostCarInfoVisualRefresh(packet, character, answer.Items);
        }

        private static void SendInitialLocalVisualRefresh(Packet packet, User user, Character character)
        {
            if (packet == null || packet.Sender == null || user == null || character == null ||
                character.ActiveCar == null || user.VehicleSerial == 0)
                return;

            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);

            var vehicle = character.ActiveCar;
            var refresh = new VisualUpdateAnswer
            {
                Serial = user.VehicleSerial,
                Age = 0,
                CarId = vehicle.CarId,
                VisualState = 1,
                CarInfo = new XiStrCarInfo
                {
                    CarID = vehicle.CarId,
                    CarType = vehicle.CarType,
                    BaseColor = vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor,
                    Grade = vehicle.Grade,
                    SlotType = vehicle.SlotType,
                    AuctionCnt = vehicle.AuctionCnt,
                    Mitron = vehicle.Mitron,
                    Kmh = vehicle.Kmh,
                    Color = vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor,
                    Color2 = vehicle.Color2,
                    MitronCapacity = vehicle.MitronCapacity,
                    MitronEfficiency = vehicle.MitronEfficiency,
                    AuctionOn = vehicle.AuctionOn,
                    SBBOn = vehicle.SBBOn
                }
            };

            packet.Sender.Send(refresh.CreatePacket());
            Log.Debug(
                "Initial visual refresh authoritative: CID={0} Serial={1} CarId={2} Color={3} -> 1201+1061",
                character.Id, user.VehicleSerial, vehicle.CarId,
                vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor);
        }

        private static void SendPostCarInfoVisualRefresh(Packet packet, Character character,
            IEnumerable<InventoryVisualItem> inventory)
        {
            if (packet == null || packet.Sender == null || character == null || character.ActiveCar == null)
                return;

            var activeCarId = character.ActiveCar.CarId;
            var rows = new List<InventoryVisualItem>();
            foreach (var item in inventory)
            {
                // IMPORTANT: 1202 is a live visual callback, not merely an inventory dump.
                // Only equipped rows are allowed to participate in this replay. ItemState=0
                // rows stay visible in 1201 inventory but must never be reapplied to the car.
                if (item != null && item.CarId == activeCarId && item.ItemState == 1)
                    rows.Add(item);
            }

            if (rows.Count == 0)
            {
                Log.Debug(
                    "Initial visual post-refresh: CID={0} CarId={1} EquippedCount=0 (no 1202 replay)",
                    character.Id, activeCarId);
                return;
            }

            var delta = new Packet((ushort)1202);
            delta.Writer.Write(rows.Count);
            foreach (var item in rows)
            {
                delta.Writer.Write(item);
                delta.Writer.Write(0); // add/update active equipped row
            }
            packet.Sender.Send(delta);

            Log.Debug(
                "Initial visual post-refresh: CID={0} CarId={1} EquippedCount={2} -> 1061+1202",
                character.Id, activeCarId, rows.Count);
        }
    }
}
