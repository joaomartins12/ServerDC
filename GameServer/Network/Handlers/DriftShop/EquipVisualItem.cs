using System;
using System.Globalization;
using System.Text.RegularExpressions;
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

        [Packet(Packets.CmdDropVisualItem)]
        public static void Drop(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null) return;

            var shopId = packet.Reader.ReadUInt32();
            var inventoryIndex = packet.Reader.ReadUInt32();
            uint carId = 0;
            var wasEquipped = false;

            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            {
                using (var lookup = new MySqlCommand(@"
SELECT CarId,ItemState
FROM dbo.visual_items
WHERE CharacterId=@cid AND ShopId=@shopId AND InventoryIndex=@inven;", conn))
                {
                    lookup.Parameters.AddWithValue("@cid", character.Id);
                    lookup.Parameters.AddWithValue("@shopId", shopId);
                    lookup.Parameters.AddWithValue("@inven", inventoryIndex);
                    using (var reader = lookup.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            packet.Sender.SendError("visual_item_not_found");
                            return;
                        }
                        carId = unchecked((uint)Convert.ToInt64(reader[0], CultureInfo.InvariantCulture));
                        wasEquipped = Convert.ToInt32(reader[1], CultureInfo.InvariantCulture) != 0;
                    }
                }

                using (var delete = new MySqlCommand(@"
DELETE FROM dbo.visual_items
WHERE CharacterId=@cid AND ShopId=@shopId AND InventoryIndex=@inven;", conn))
                {
                    delete.Parameters.AddWithValue("@cid", character.Id);
                    delete.Parameters.AddWithValue("@shopId", shopId);
                    delete.Parameters.AddWithValue("@inven", inventoryIndex);
                    delete.ExecuteNonQuery();
                }
            }

            // Retail pairs CmdDropVisualItem 1211 with 1212. Echo the two request keys.
            var ack = new Packet((ushort)1212);
            ack.Writer.Write(shopId);
            ack.Writer.Write(inventoryIndex);
            packet.Sender.Send(ack);

            global::GameServer.Network.Handlers.Join.VisualItemList.SendCurrent(packet);
            if (wasEquipped) VisualShopWorldSync.Broadcast(packet.Sender.User);
            CheckStat.Handle(packet);

            Log.Info("Visual item dropped: CID={0} ShopId={1} CarId={2} InvenIdx={3} Equipped={4}",
                character.Id, shopId, carId, inventoryIndex, wasEquipped);
        }
    }

    internal static class VisualShopCatalogRecovery
    {
        /// <summary>
        /// Some retail XLT rows (notably paint helper rows such as i_g_paint_S15)
        /// contain no direct price even though the shop UI sells them. Infer only from
        /// another row in the same retail category/tier, and store the result as a
        /// Server* override so the original imported Source* columns remain untouched.
        /// </summary>
        public static bool TryInferMissingMitoPrice(MySqlConnection conn, uint shopId, int period)
        {
            if (conn == null) return false;

            string itemCode;
            string category;
            int mainCategory;
            bool useMito;
            using (var target = new MySqlCommand(@"
SELECT ItemCode,Category,ISNULL(MainCategoryId,0),UseMito
FROM dbo.visual_item_catalog
WHERE ShopId=@shopId;", conn))
            {
                target.Parameters.AddWithValue("@shopId", shopId);
                using (var r = target.ExecuteReader())
                {
                    if (!r.Read()) return false;
                    itemCode = r.IsDBNull(0) ? string.Empty : Convert.ToString(r[0]);
                    category = r.IsDBNull(1) ? string.Empty : Convert.ToString(r[1]);
                    mainCategory = Convert.ToInt32(r[2], CultureInfo.InvariantCulture);
                    useMito = Convert.ToBoolean(r[3], CultureInfo.InvariantCulture);
                }
            }

            if (!useMito) return false;
            var normalized = (itemCode ?? string.Empty).ToLowerInvariant();
            if (!normalized.Contains("paint")) return false;

            string sourceColumn;
            string serverColumn;
            switch (period)
            {
                case 0: sourceColumn = "SourceMitoPrice"; serverColumn = "ServerMitoPrice"; break;
                case 1: sourceColumn = "SourceMito7dPrice"; serverColumn = "ServerMito7dPrice"; break;
                case 2: sourceColumn = "SourceMito30dPrice"; serverColumn = "ServerMito30dPrice"; break;
                case 3: sourceColumn = "SourceMito90dPrice"; serverColumn = "ServerMito90dPrice"; break;
                case 4: sourceColumn = "SourceMito0dPrice"; serverColumn = "ServerMito0dPrice"; break;
                default: return false;
            }

            var tierMatch = Regex.Match(itemCode ?? string.Empty, @"(?:^|_)S(\d+)(?:$|_)", RegexOptions.IgnoreCase);
            var tier = tierMatch.Success ? "S" + tierMatch.Groups[1].Value : string.Empty;
            var price = 0;

            var sql = string.Format(CultureInfo.InvariantCulture, @"
SELECT TOP 1 COALESCE({0},{1}) AS Price
FROM dbo.visual_item_catalog
WHERE ShopId<>@shopId AND UseMito=1
  AND COALESCE({0},{1},0)>0
  AND ((@category<>'' AND Category=@category) OR (@mainCategory>0 AND MainCategoryId=@mainCategory))
  AND (@tier='' OR ItemCode LIKE @tierPattern OR DisplayName LIKE @tierPattern)
ORDER BY CASE WHEN Category=@category THEN 0 ELSE 1 END,
         CASE WHEN ItemCode LIKE '%paint%' THEN 0 ELSE 1 END,
         ShopId;", serverColumn, sourceColumn);

            using (var candidate = new MySqlCommand(sql, conn))
            {
                candidate.Parameters.AddWithValue("@shopId", shopId);
                candidate.Parameters.AddWithValue("@category", category ?? string.Empty);
                candidate.Parameters.AddWithValue("@mainCategory", mainCategory);
                candidate.Parameters.AddWithValue("@tier", tier);
                candidate.Parameters.AddWithValue("@tierPattern", string.IsNullOrEmpty(tier) ? "%" : "%" + tier + "%");
                var raw = candidate.ExecuteScalar();
                if (raw != null && raw != DBNull.Value)
                    price = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }

            // S15 is a retail premium visual tier. If its malformed paint row has no
            // usable peer in this client build, the neighboring S15 visual rows establish
            // the 7-day tier at 30,000 Mito. Keep this as a Server override, never Source.
            if (price <= 0 && tier.Equals("S15", StringComparison.OrdinalIgnoreCase) && period == 1)
                price = 30000;

            if (price <= 0) return false;

            var updateSql = string.Format(CultureInfo.InvariantCulture,
                "UPDATE dbo.visual_item_catalog SET {0}=@price, UpdatedUtc=SYSUTCDATETIME() WHERE ShopId=@shopId AND {0} IS NULL;",
                serverColumn);
            using (var update = new MySqlCommand(updateSql, conn))
            {
                update.Parameters.AddWithValue("@price", price);
                update.Parameters.AddWithValue("@shopId", shopId);
                update.ExecuteNonQuery();
            }

            Log.Warning("VisualShop recovered missing retail paint price: ShopId={0} ItemCode={1} Tier={2} Period={3} Mito={4} Override={5}",
                shopId, itemCode ?? string.Empty, tier, period, price, serverColumn);
            return true;
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
