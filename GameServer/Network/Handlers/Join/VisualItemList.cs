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
        /// active car's persisted paint/tint. Do not replay the same rows through 1202:
        /// 1202 is a modification stream and replaying every equipped item after a shop
        /// preview makes the client keep transient preview state as if it were committed.
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
            SendInitialLocalVisualRefresh(packet, user, character);
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
                "Visual inventory authoritative refresh: CID={0} Serial={1} CarId={2} Color=0x{3:X6} -> 1201+1061",
                character.Id, user.VehicleSerial, vehicle.CarId, vehicle.Color);
        }
    }
}
