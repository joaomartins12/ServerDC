using System;
using System.Globalization;
using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public static class EquipVisualItem
    {
        [Packet(Packets.CmdEquipVisualItem)]
        public static void Equip(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null) return;

            var inventoryIndex = packet.Reader.ReadUInt32();
            var previousIndex = packet.Reader.ReadInt32();
            var carId = packet.Reader.ReadUInt32();

            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            {
                int category;
                using (var lookup = new MySqlCommand(@"
SELECT CategoryIndex
FROM dbo.visual_items
WHERE CharacterId=@cid AND CarId=@carId AND InventoryIndex=@inven;", conn))
                {
                    lookup.Parameters.AddWithValue("@cid", character.Id);
                    lookup.Parameters.AddWithValue("@carId", carId);
                    lookup.Parameters.AddWithValue("@inven", inventoryIndex);
                    var value = lookup.ExecuteScalar();
                    if (value == null || value == DBNull.Value)
                    {
                        packet.Sender.SendError("visual_item_not_found");
                        return;
                    }
                    category = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }

                if (category > 0)
                {
                    using (var unequip = new MySqlCommand(@"
UPDATE dbo.visual_items
SET ItemState=0, UpdateTime=@now
WHERE CharacterId=@cid AND CarId=@carId AND CategoryIndex=@category AND ItemState=1;", conn))
                    {
                        unequip.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        unequip.Parameters.AddWithValue("@cid", character.Id);
                        unequip.Parameters.AddWithValue("@carId", carId);
                        unequip.Parameters.AddWithValue("@category", category);
                        unequip.ExecuteNonQuery();
                    }
                }

                using (var equip = new MySqlCommand(@"
UPDATE dbo.visual_items
SET ItemState=1, UpdateTime=@now
WHERE CharacterId=@cid AND CarId=@carId AND InventoryIndex=@inven;", conn))
                {
                    equip.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    equip.Parameters.AddWithValue("@cid", character.Id);
                    equip.Parameters.AddWithValue("@carId", carId);
                    equip.Parameters.AddWithValue("@inven", inventoryIndex);
                    equip.ExecuteNonQuery();
                }
            }

            var ack = new Packet(1206);
            ack.Writer.Write(inventoryIndex);
            ack.Writer.Write(previousIndex);
            ack.Writer.Write(carId);
            packet.Sender.Send(ack);

            global::GameServer.Network.Handlers.Join.VisualItemList.SendCurrent(packet);
            VisualShopWorldSync.Broadcast(packet.Sender.User);
            CheckStat.Handle(packet);

            Log.Info("Visual item equipped: CID={0} CarId={1} InvenIdx={2}", character.Id, carId, inventoryIndex);
        }

        [Packet(Packets.CmdUnEquipVisualItem)]
        public static void UnEquip(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null) return;

            var inventoryIndex = packet.Reader.ReadUInt32();
            var carId = packet.Reader.ReadUInt32();

            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            using (var cmd = new MySqlCommand(@"
UPDATE dbo.visual_items
SET ItemState=0, UpdateTime=@now
WHERE CharacterId=@cid AND CarId=@carId AND InventoryIndex=@inven;", conn))
            {
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@cid", character.Id);
                cmd.Parameters.AddWithValue("@carId", carId);
                cmd.Parameters.AddWithValue("@inven", inventoryIndex);
                cmd.ExecuteNonQuery();
            }

            var ack = new Packet(1208);
            ack.Writer.Write(inventoryIndex);
            ack.Writer.Write(carId);
            packet.Sender.Send(ack);

            global::GameServer.Network.Handlers.Join.VisualItemList.SendCurrent(packet);
            VisualShopWorldSync.Broadcast(packet.Sender.User);
            CheckStat.Handle(packet);

            Log.Info("Visual item unequipped: CID={0} CarId={1} InvenIdx={2}", character.Id, carId, inventoryIndex);
        }
    }

    internal static class VisualShopWorldSync
    {
        public static void Broadcast(Shared.Objects.User sourceUser)
        {
            if (sourceUser == null || sourceUser.ActiveCharacter == null ||
                global::GameServer.GameServer.Instance.Server == null)
                return;

            var sent = 0;
            foreach (var client in global::GameServer.GameServer.Instance.Server.GetClients())
            {
                if (client == null || client.User == null)
                    continue;

                var visual = PlayerVisualSnapshotBuilder.BuildRoomNotifyChange(
                    sourceUser.VehicleSerial,
                    sourceUser.ActiveCharacter);
                client.Send(visual.CreatePacket());
                sent++;
            }

            Log.Debug("Visual world sync: CID={0} Serial={1} Recipients={2}",
                sourceUser.ActiveCharacter.Id, sourceUser.VehicleSerial, sent);
        }
    }
}
