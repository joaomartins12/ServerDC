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

            Log.Debug("VisualItemListAck: CID={0} Count={1}", character.Id, answer.Items.Count);
            packet.Sender.Send(answer.CreatePacket());

            // Retail keeps inventory state (1201) and the rendered local vehicle on
            // separate paths. Loading the visual list alone populates the inventory but
            // does not force the already-created car object to rebuild. This is why a
            // saved paint/tint only became visible after toggling a body kit. Once 1201
            // has populated the client's VS inventory, immediately refresh the active
            // XiStrCarInfo through packet 1061.
            SendInitialLocalVisualRefresh(packet, user, character);
        }

        private static void SendInitialLocalVisualRefresh(Packet packet, User user, Character character)
        {
            if (packet == null || packet.Sender == null || user == null || character == null ||
                character.ActiveCar == null || user.VehicleSerial == 0)
                return;

            // Resolve the equipped paint before serializing XiStrCarInfo so reload/login
            // starts with the same persisted color that subsequent runtime visual updates
            // use. VisualState=1 is the retail dirty/active state used here deliberately to
            // make the client invalidate and rebuild the local render object after 1201.
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
                    BaseColor = vehicle.BaseColor,
                    Grade = vehicle.Grade,
                    SlotType = vehicle.SlotType,
                    AuctionCnt = vehicle.AuctionCnt,
                    Mitron = vehicle.Mitron,
                    Kmh = vehicle.Kmh,
                    Color = vehicle.Color,
                    Color2 = vehicle.Color2,
                    MitronCapacity = vehicle.MitronCapacity,
                    MitronEfficiency = vehicle.MitronEfficiency,
                    AuctionOn = vehicle.AuctionOn,
                    SBBOn = vehicle.SBBOn
                }
            };

            packet.Sender.Send(refresh.CreatePacket());
            Log.Debug(
                "Initial visual refresh: CID={0} Serial={1} CarId={2} Color={3} VisualState=1 -> 1201+1061",
                character.Id, user.VehicleSerial, vehicle.CarId, vehicle.Color);
        }
    }
}
