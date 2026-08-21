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
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
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
        }
    }
}
