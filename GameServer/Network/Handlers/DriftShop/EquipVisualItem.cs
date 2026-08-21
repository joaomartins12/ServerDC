using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
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
            var requestedCarId = packet.Reader.ReadUInt32();

            VisualShopProtocolSync.VisualRow row;
            uint targetCarId;
            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            {
                row = VisualShopProtocolSync.LoadByInventory(conn, character.Id, inventoryIndex);
                if (row == null)
                {
                    packet.Sender.SendError("visual_item_not_found");
                    return;
                }

                targetCarId = requestedCarId != 0 ? requestedCarId : row.CarId;
                using (var owns = new MySqlCommand(
                    "SELECT COUNT(1) FROM dbo.vehicles WHERE CID=@carId AND CharID=@charId;", conn))
                {
                    owns.Parameters.AddWithValue("@carId", targetCarId);
                    owns.Parameters.AddWithValue("@charId", character.Id);
                    if (Convert.ToInt32(owns.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    {
                        packet.Sender.SendError("not_your_car");
                        return;
                    }
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (row.CategoryIndex > 0)
                {
                    using (var unequip = new MySqlCommand(@"
UPDATE dbo.visual_items
SET ItemState=0, UpdateTime=@now
WHERE CharacterId=@cid AND CarId=@carId AND CategoryIndex=@category AND ItemState=1;", conn))
                    {
                        unequip.Parameters.AddWithValue("@now", now);
                        unequip.Parameters.AddWithValue("@cid", character.Id);
                        unequip.Parameters.AddWithValue("@carId", targetCarId);
                        unequip.Parameters.AddWithValue("@category", row.CategoryIndex);
                        unequip.ExecuteNonQuery();
                    }
                }

                using (var equip = new MySqlCommand(@"
UPDATE dbo.visual_items
SET CarId=@carId, ItemState=1, UpdateTime=@now
WHERE CharacterId=@cid AND InventoryIndex=@inven;", conn))
                {
                    equip.Parameters.AddWithValue("@carId", targetCarId);
                    equip.Parameters.AddWithValue("@now", now);
                    equip.Parameters.AddWithValue("@cid", character.Id);
                    equip.Parameters.AddWithValue("@inven", inventoryIndex);
                    if (equip.ExecuteNonQuery() == 0)
                    {
                        packet.Sender.SendError("visual_item_not_found");
                        return;
                    }
                }
            }

            var oldCarId = row.CarId;
            row.CarId = targetCarId;
            row.Item.CarId = targetCarId;
            row.Item.ItemState = 1;

            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);

            var ack = new Packet((ushort)1206);
            ack.Writer.Write(inventoryIndex);
            ack.Writer.Write(previousIndex);
            ack.Writer.Write(targetCarId);
            packet.Sender.Send(ack);

            VisualShopProtocolSync.SendCategory(packet, character.Id, targetCarId, row.CategoryIndex);
            VisualShopWorldSync.Sync(packet.Sender.User);
            CheckStat.Handle(packet);

            Log.Info(
                "Visual item equipped: CID={0} RequestedCarId={1} OldCarId={2} TargetCarId={3} Category={4} InvenIdx={5}",
                character.Id, requestedCarId, oldCarId, targetCarId, row.CategoryIndex, inventoryIndex);
        }

        [Packet(Packets.CmdUnEquipVisualItem)]
        public static void UnEquip(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null) return;

            var inventoryIndex = packet.Reader.ReadUInt32();
            var requestedCarId = packet.Reader.ReadUInt32();

            VisualShopProtocolSync.VisualRow row;
            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            {
                row = VisualShopProtocolSync.LoadByInventory(conn, character.Id, inventoryIndex);
                if (row == null)
                {
                    packet.Sender.SendError("visual_item_not_found");
                    return;
                }

                using (var cmd = new MySqlCommand(@"
UPDATE dbo.visual_items
SET ItemState=0, UpdateTime=@now
WHERE CharacterId=@cid AND InventoryIndex=@inven;", conn))
                {
                    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    cmd.Parameters.AddWithValue("@cid", character.Id);
                    cmd.Parameters.AddWithValue("@inven", inventoryIndex);
                    if (cmd.ExecuteNonQuery() == 0)
                    {
                        packet.Sender.SendError("visual_item_not_found");
                        return;
                    }
                }
            }

            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);

            var ack = new Packet((ushort)1208);
            ack.Writer.Write(inventoryIndex);
            ack.Writer.Write(row.CarId);
            packet.Sender.Send(ack);

            VisualShopProtocolSync.SendCategory(packet, character.Id, row.CarId, row.CategoryIndex);
            VisualShopWorldSync.Sync(packet.Sender.User);
            CheckStat.Handle(packet);

            Log.Info(
                "Visual item unequipped: CID={0} RequestedCarId={1} RealCarId={2} Category={3} InvenIdx={4}",
                character.Id, requestedCarId, row.CarId, row.CategoryIndex, inventoryIndex);
        }

        [Packet(Packets.CmdDropVisualItem)]
        public static void Drop(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null) return;

            var shopId = packet.Reader.ReadUInt32();
            var inventoryIndex = packet.Reader.ReadUInt32();

            VisualShopProtocolSync.VisualRow row;
            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            {
                row = VisualShopProtocolSync.LoadByInventory(conn, character.Id, inventoryIndex);
                if (row == null || row.Item.TableIdx != shopId)
                {
                    packet.Sender.SendError("visual_item_not_found");
                    return;
                }

                using (var delete = new MySqlCommand(@"
DELETE FROM dbo.visual_items
WHERE CharacterId=@cid AND InventoryIndex=@inven;", conn))
                {
                    delete.Parameters.AddWithValue("@cid", character.Id);
                    delete.Parameters.AddWithValue("@inven", inventoryIndex);
                    if (delete.ExecuteNonQuery() == 0)
                    {
                        packet.Sender.SendError("visual_item_not_found");
                        return;
                    }
                }
            }

            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);

            var ack = new Packet((ushort)1212);
            ack.Writer.Write(shopId);
            ack.Writer.Write(inventoryIndex);
            packet.Sender.Send(ack);

            VisualShopProtocolSync.SendDelete(packet, row.Item);
            if (row.Item.ItemState != 0)
                VisualShopWorldSync.Sync(packet.Sender.User);
            CheckStat.Handle(packet);

            Log.Info("Visual item dropped: CID={0} ShopId={1} CarId={2} InvenIdx={3} Equipped={4}",
                character.Id, shopId, row.CarId, inventoryIndex, row.Item.ItemState != 0);
        }
    }

    internal static class VisualShopProtocolSync
    {
        private const ushort VsItemModListAck = 1202;
        private const int ModAddOrUpdate = 0;
        private const int ModDelete = 2;

        internal sealed class VisualRow
        {
            public InventoryVisualItem Item;
            public uint CarId;
            public int CategoryIndex;
        }

        public static VisualRow LoadByInventory(MySqlConnection conn, ulong characterId, uint inventoryIndex)
        {
            using (var cmd = new MySqlCommand(@"
SELECT CarId,ItemState,ShopId,InventoryIndex,Data,Period,UpdateTime,CreateTime,CategoryIndex
FROM dbo.visual_items
WHERE CharacterId=@cid AND InventoryIndex=@inven;", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@inven", inventoryIndex);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return ReadRow(r);
                }
            }
        }

        public static void SendInventory(Packet packet, ulong characterId, uint inventoryIndex)
        {
            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            {
                var row = LoadByInventory(conn, characterId, inventoryIndex);
                if (row == null) return;
                Send(packet, new[] { row.Item }, ModAddOrUpdate, "inventory=" + inventoryIndex);
            }
        }

        public static void SendCategory(Packet packet, ulong characterId, uint carId, int categoryIndex)
        {
            if (categoryIndex <= 0) return;

            var items = new List<InventoryVisualItem>();
            using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
            using (var cmd = new MySqlCommand(@"
SELECT CarId,ItemState,ShopId,InventoryIndex,Data,Period,UpdateTime,CreateTime,CategoryIndex
FROM dbo.visual_items
WHERE CharacterId=@cid AND CarId=@carId AND CategoryIndex=@category
ORDER BY InventoryIndex;", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@carId", carId);
                cmd.Parameters.AddWithValue("@category", categoryIndex);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        items.Add(ReadRow(r).Item);
                }
            }

            if (items.Count == 0) return;
            Send(packet, items, ModAddOrUpdate, "car=" + carId + " category=" + categoryIndex);
        }

        public static void SendDelete(Packet packet, InventoryVisualItem item)
        {
            if (item == null) return;
            Send(packet, new[] { item }, ModDelete, "delete inventory=" + item.InvenIdx);
        }

        private static void Send(Packet requestPacket, IEnumerable<InventoryVisualItem> items, int modType, string context)
        {
            var list = new List<InventoryVisualItem>(items);
            if (list.Count == 0) return;

            var ack = new Packet(VsItemModListAck);
            ack.Writer.Write(list.Count);
            foreach (var item in list)
            {
                ack.Writer.Write(item);
                ack.Writer.Write(modType);
            }
            requestPacket.Sender.Send(ack);

            Log.Debug("VSItemModList 1202: Count={0} ModType={1} {2}",
                list.Count, modType, context ?? string.Empty);
        }

        private static VisualRow ReadRow(System.Data.IDataRecord r)
        {
            var carId = unchecked((uint)Convert.ToInt64(r[0], CultureInfo.InvariantCulture));
            return new VisualRow
            {
                CarId = carId,
                CategoryIndex = Convert.ToInt32(r[8], CultureInfo.InvariantCulture),
                Item = new InventoryVisualItem
                {
                    CarId = carId,
                    ItemState = Convert.ToInt32(r[1], CultureInfo.InvariantCulture),
                    TableIdx = unchecked((uint)Convert.ToInt32(r[2], CultureInfo.InvariantCulture)),
                    InvenIdx = unchecked((uint)Convert.ToInt32(r[3], CultureInfo.InvariantCulture)),
                    PlateName = r.IsDBNull(4) ? string.Empty : Convert.ToString(r[4], CultureInfo.InvariantCulture),
                    Period = Convert.ToInt32(r[5], CultureInfo.InvariantCulture),
                    UpdateTime = ClampInt64ToInt32(Convert.ToInt64(r[6], CultureInfo.InvariantCulture)),
                    CreateTime = ClampInt64ToInt32(Convert.ToInt64(r[7], CultureInfo.InvariantCulture))
                }
            };
        }

        private static int ClampInt64ToInt32(long value)
        {
            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }
    }

    internal static class VisualShopCatalogRecovery
    {
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

            var normalized = (itemCode ?? string.Empty).ToLowerInvariant();
            var isPaint = normalized.Contains("paint");
            if (!useMito && !isPaint) return false;
            if (!isPaint) return false;

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

            if (price <= 0 && tier.Equals("S15", StringComparison.OrdinalIgnoreCase) && period == 1)
                price = 30000;
            if (price <= 0) return false;

            var updateSql = string.Format(CultureInfo.InvariantCulture,
                "UPDATE dbo.visual_item_catalog SET UseMito=1, {0}=@price, UpdatedUtc=SYSUTCDATETIME() WHERE ShopId=@shopId;",
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
        public static void Sync(User sourceUser)
        {
            if (sourceUser == null || sourceUser.ActiveCharacter == null ||
                sourceUser.ActiveCharacter.ActiveCar == null ||
                global::GameServer.GameServer.Instance.Server == null)
                return;

            var character = sourceUser.ActiveCharacter;
            PlayerVisualSnapshotBuilder.ApplyActivePaint(character);

            var ownerSent = false;
            var remoteSent = 0;

            foreach (var client in global::GameServer.GameServer.Instance.Server.GetClients())
            {
                if (client == null || client.User == null)
                    continue;

                if (ReferenceEquals(client.User, sourceUser))
                {
                    client.Send(BuildLocalVisualUpdate(sourceUser).CreatePacket());
                    ownerSent = true;
                    continue;
                }

                // Retail packet 802 carries the 216-byte XiPlayerInfo, including the
                // equipped XiVisualItem snapshot. This is the player identity/appearance
                // channel used by the free-roam player manager. Packet 467 belongs to
                // the Battle Zone room protocol and must not be used for world cosmetics.
                client.Send(new PlayerInfoOldAnswer
                {
                    PlayerInfo = PlayerVisualSnapshotBuilder.BuildPlayerInfo(
                        sourceUser.VehicleSerial, character)
                }.CreatePacket());
                remoteSent++;
            }

            Log.Debug("Visual retail sync: CID={0} Serial={1} Owner1061={2} Remote802={3}",
                character.Id, sourceUser.VehicleSerial, ownerSent, remoteSent);
        }

        public static void Broadcast(User sourceUser)
        {
            Sync(sourceUser);
        }

        private static VisualUpdateAnswer BuildLocalVisualUpdate(User user)
        {
            var character = user.ActiveCharacter;
            var vehicle = character.ActiveCar;
            return new VisualUpdateAnswer
            {
                Serial = user.VehicleSerial,
                Age = 0,
                CarId = vehicle.CarId,
                VisualState = 0,
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
        }
    }
}
