using GameServer.Util;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class BuyVisualItemThread
    {
        [Packet(Packets.CmdBuyVisualItemThread)]
        public static void Handle(Packet packet)
        {
            var request = new BuyVisualItemThreadPacket(packet);
            var user = packet.Sender.User;
            var character = user == null ? null : user.ActiveCharacter;
            if (character == null)
            {
                packet.Sender.SendError("Failed to purchase item!");
                return;
            }

            VisualShopDatabase.PurchaseResult purchase;
            using (var conn = GameServer.Instance.Database.Connection)
            {
                purchase = VisualShopDatabase.Purchase(
                    conn,
                    character.Id,
                    request.CarId,
                    request.TableIndex,
                    unchecked((int)request.PeriodIdx),
                    request.UseMileage,
                    request.Cash,
                    request.PlateName);
            }

            if (!purchase.Success)
            {
                Log.Warning(
                    "BuyVisualItem rejected: CID={0} ShopId={1} CarId={2} Period={3} Mileage={4} ClientCash={5} Reason={6}",
                    character.Id,
                    request.TableIndex,
                    request.CarId,
                    request.PeriodIdx,
                    request.UseMileage,
                    request.Cash,
                    purchase.Error ?? "unknown");
                packet.Sender.SendError("Failed to purchase item!");
                return;
            }

            // The SQL transaction is authoritative. Keep the already-loaded character
            // snapshot in sync so subsequent packets in this session show the new balance.
            switch (purchase.Currency)
            {
                case VisualShopDatabase.CurrencyType.Mito:
                    character.MitoMoney -= purchase.Price;
                    break;
                case VisualShopDatabase.CurrencyType.Hancoin:
                    character.Hancoin -= purchase.Price;
                    break;
                case VisualShopDatabase.CurrencyType.Mileage:
                    character.TotalDistance -= purchase.Price;
                    break;
            }

            var ack = new BuyVisualItemThreadAnswer
            {
                Type = purchase.Support,
                TableIndex = purchase.ShopId,
                CarId = purchase.CarId,
                InventoryId = unchecked((int)purchase.InventoryIndex),
                Period = purchase.Period,
                Mito = purchase.Currency == VisualShopDatabase.CurrencyType.Mito ? purchase.Price : 0,
                Hancoin = purchase.Currency == VisualShopDatabase.CurrencyType.Hancoin ? purchase.Price : 0,
                BonusMito = purchase.BonusMito,
                Mileage = purchase.Currency == VisualShopDatabase.CurrencyType.Mileage ? purchase.Price : 0
            };
            packet.Sender.Send(ack.CreatePacket());

            // The leaked handler sends a visual refresh and StatUpdate immediately after
            // auto-equipping the newly bought item. Packet 467 is the renderer refresh
            // already used by this emulator; CheckStat sends packet 760.
            if (purchase.Equipped)
            {
                var visual = PlayerVisualSnapshotBuilder.BuildRoomNotifyChange(user.VehicleSerial, character);
                packet.Sender.Send(visual.CreatePacket());
            }

            CheckStat.Handle(packet);
        }
    }
}
