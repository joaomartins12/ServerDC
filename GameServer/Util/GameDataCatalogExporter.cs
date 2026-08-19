using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    /// <summary>
    /// Exports the game-data tables into human-readable CSV/text files.
    /// These files are intended for packet/protocol reverse engineering: packet numeric table indexes
    /// can be matched directly to the textual ids and names used by the client data files.
    /// </summary>
    public static class GameDataCatalogExporter
    {
        public static void Export(
            IList<BasicItem> items,
            IList<VShopItemList.VShopItem> visualItems,
            IList<VehicleList.VehicleData> vehicles,
            IList<QuestTable.Quest> quests,
            IDictionary<int, KeyValuePair<ushort, long>> levelTable)
        {
            try
            {
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Catalogs");
                Directory.CreateDirectory(root);

                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                var sessionRoot = Path.Combine(root, stamp);
                Directory.CreateDirectory(sessionRoot);

                var itemById = (items ?? new List<BasicItem>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

                ExportItems(sessionRoot, items);
                ExportVShop(sessionRoot, visualItems, itemById);
                ExportVehicles(sessionRoot, vehicles);
                ExportQuestRewards(sessionRoot, quests, itemById);
                ExportLevels(sessionRoot, levelTable);
                ExportSummary(sessionRoot, items, visualItems, vehicles, quests, levelTable, itemById);

                WritePointer(root, sessionRoot);

                Log.Info("Game data catalog exported to {0}", sessionRoot);
                Log.Info("Catalog counts: Items={0}, VShop={1}, Vehicles={2}, Quests={3}, Levels={4}",
                    items == null ? 0 : items.Count,
                    visualItems == null ? 0 : visualItems.Count,
                    vehicles == null ? 0 : vehicles.Count,
                    quests == null ? 0 : quests.Count,
                    levelTable == null ? 0 : levelTable.Count);
            }
            catch (Exception ex)
            {
                Log.Warning("Unable to export game data catalog: {0}", ex.Message);
            }
        }

        private static void ExportItems(string root, IList<BasicItem> items)
        {
            using (var writer = Csv(Path.Combine(root, "Items_RuntimeTable.csv")))
            {
                writer.WriteLine("TableIndex,SourceType,Id,Name,Category,BuyValue,SellValue,ExpirationTime,Auctionable,PartsShop,Sendable,Grade,RequiredLevel,BasePoints,BasePointModifier,BasePointVariable,PartAssist,MaxStack,Stat,Cooldown,Duration");

                if (items == null) return;

                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    var part = item as ItemTable.Item;
                    var use = item as UseItemTable.UseItem;

                    WriteRow(writer,
                        i,
                        part != null ? "Item" : use != null ? "UseItem" : "BasicItem",
                        item.Id,
                        item.Name,
                        item.Category,
                        item.BuyValue,
                        item.SellValue,
                        item.ExpirationTime,
                        item.Auctionable,
                        item.PartsShop,
                        item.Sendable,
                        part == null ? "" : part.Grade,
                        part == null ? "" : part.RequiredLevel,
                        part == null ? "" : part.BasePoints,
                        part == null ? "" : part.BasePointModifier,
                        part == null ? "" : part.BasePointVariable,
                        part == null ? "" : part.PartAssist,
                        use == null ? "" : use.MaxStack,
                        use == null ? "" : use.StatModifier,
                        use == null ? "" : use.CooldownTime,
                        use == null ? "" : use.Duration);
                }
            }
        }

        private static void ExportVShop(string root, IList<VShopItemList.VShopItem> shop,
            IDictionary<string, BasicItem> itemById)
        {
            using (var writer = Csv(Path.Combine(root, "VShop_Resolved.csv")))
            {
                writer.WriteLine("ShopId,ItemCode,Resolved,ResolvedName,ResolvedCategory,Category,CategoryIndex,UseMito,MitoPrice,MitoSell,Mito7D,Mito30D,Mito90D,Mito365D,Mito0D,UseHancoin,Hancoin7D,Hancoin30D,Hancoin90D,Hancoin365D,Hancoin0D");
                if (shop == null) return;

                foreach (var entry in shop)
                {
                    if (entry == null) continue;
                    BasicItem resolved;
                    var found = !string.IsNullOrWhiteSpace(entry.ItemName) && itemById.TryGetValue(entry.ItemName, out resolved);
                    if (!found) resolved = null;

                    WriteRow(writer,
                        entry.UniqueId,
                        entry.ItemName,
                        found ? "YES" : "NO",
                        resolved == null ? "" : resolved.Name,
                        resolved == null ? "" : resolved.Category,
                        entry.Category,
                        entry.CategoryIndex,
                        entry.UseMito,
                        entry.MitoPrice,
                        entry.SellMitoPrice,
                        entry.Mito7dPrice,
                        entry.Mito30dPrice,
                        entry.Mito90dPrice,
                        entry.Mito365dPrice,
                        entry.Mito0dPrice,
                        entry.UseHancoin,
                        entry.Hancoin7dPrice,
                        entry.Hancoin30dPrice,
                        entry.Hancoin90dPrice,
                        entry.Hancoin365dPrice,
                        entry.Hancoin0dPrice);
                }
            }
        }

        private static void ExportVehicles(string root, IList<VehicleList.VehicleData> vehicles)
        {
            using (var writer = Csv(Path.Combine(root, "Vehicles.csv")))
            using (var upgrades = Csv(Path.Combine(root, "Vehicle_Upgrades.csv")))
            {
                writer.WriteLine("RuntimeIndex,VehicleId,Name,Type,TypeString,Sellable,Grade,Accel,Speed,Crash,Boost,RequiredLevel,Level,UpgradeCount");
                upgrades.WriteLine("VehicleRuntimeIndex,VehicleId,VehicleName,UpgradeGrade,Coupon,Accel,Speed,Crash,Boost,Price,Sell,CloseSell,UpgradeMito,Efficiency,Capacity,RequiredLevel");

                if (vehicles == null) return;

                for (var i = 0; i < vehicles.Count; i++)
                {
                    var vehicle = vehicles[i];
                    if (vehicle == null) continue;

                    WriteRow(writer, i, vehicle.UniqueId, vehicle.Name, vehicle.Type, vehicle.TypeStr, vehicle.Sellable,
                        vehicle.Grade, vehicle.Acceleration, vehicle.Speed, vehicle.Crash, vehicle.Boost,
                        vehicle.RequiredLevel, vehicle.Level, vehicle.Upgrades == null ? 0 : vehicle.Upgrades.Count);

                    if (vehicle.Upgrades == null) continue;
                    for (var grade = 0; grade < vehicle.Upgrades.Count; grade++)
                    {
                        var up = vehicle.Upgrades[grade];
                        if (up == null) continue;
                        WriteRow(upgrades, i, vehicle.UniqueId, vehicle.Name, grade, up.Coupon, up.Acceleration,
                            up.Speed, up.Crash, up.Boost, up.Price, up.Sell, up.CloseSell, up.UpgradeMito,
                            up.Efficiency, up.Capacity, up.RequiredLevel);
                    }
                }
            }
        }

        private static void ExportQuestRewards(string root, IList<QuestTable.Quest> quests,
            IDictionary<string, BasicItem> itemById)
        {
            using (var writer = Csv(Path.Combine(root, "Quest_Rewards_Resolved.csv")))
            {
                writer.WriteLine("QuestTableIndex,QuestId,MissionType,Exp,Mito,RewardSlot,RewardId,Resolved,RewardName,RewardCategory");
                if (quests == null) return;

                foreach (var quest in quests)
                {
                    if (quest == null) continue;
                    var rewards = new[] { quest.RewardItem1, quest.RewardItem2, quest.RewardItem3 };
                    for (var slot = 0; slot < rewards.Length; slot++)
                    {
                        var rewardId = rewards[slot];
                        if (string.IsNullOrWhiteSpace(rewardId) || rewardId == "0") continue;

                        BasicItem resolved;
                        var found = itemById.TryGetValue(rewardId, out resolved);
                        WriteRow(writer, quest.TableIndex, quest.Id, quest.MissionType, quest.Experience, quest.Mito,
                            slot + 1, rewardId, found ? "YES" : "NO",
                            found ? resolved.Name : "", found ? resolved.Category : "");
                    }
                }
            }
        }

        private static void ExportLevels(string root, IDictionary<int, KeyValuePair<ushort, long>> levelTable)
        {
            using (var writer = Csv(Path.Combine(root, "Levels.csv")))
            {
                writer.WriteLine("Key,Level,Experience");
                if (levelTable == null) return;

                foreach (var pair in levelTable.OrderBy(x => x.Key))
                    WriteRow(writer, pair.Key, pair.Value.Key, pair.Value.Value);
            }
        }

        private static void ExportSummary(string root,
            IList<BasicItem> items,
            IList<VShopItemList.VShopItem> shop,
            IList<VehicleList.VehicleData> vehicles,
            IList<QuestTable.Quest> quests,
            IDictionary<int, KeyValuePair<ushort, long>> levels,
            IDictionary<string, BasicItem> itemById)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DRIFT CITY GAME DATA CATALOG");
            sb.AppendLine("============================");
            sb.AppendLine("Generated : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.AppendLine("Items     : " + (items == null ? 0 : items.Count));
            sb.AppendLine("VShop     : " + (shop == null ? 0 : shop.Count));
            sb.AppendLine("Vehicles  : " + (vehicles == null ? 0 : vehicles.Count));
            sb.AppendLine("Quests    : " + (quests == null ? 0 : quests.Count));
            sb.AppendLine("Levels    : " + (levels == null ? 0 : levels.Count));
            sb.AppendLine();

            if (items != null)
            {
                sb.AppendLine("ITEM CATEGORIES");
                foreach (var group in items.Where(x => x != null).GroupBy(x => x.Category ?? "<none>").OrderBy(x => x.Key))
                    sb.AppendLine("  " + group.Key + " = " + group.Count());
            }

            if (shop != null)
            {
                var unresolved = shop.Count(x => x != null && !string.IsNullOrWhiteSpace(x.ItemName) && !itemById.ContainsKey(x.ItemName));
                sb.AppendLine();
                sb.AppendLine("VSHOP REFERENCES");
                sb.AppendLine("  Resolved   = " + (shop.Count - unresolved));
                sb.AppendLine("  Unresolved = " + unresolved);
            }

            File.WriteAllText(Path.Combine(root, "Summary.txt"), sb.ToString(), Encoding.UTF8);
        }

        private static void WritePointer(string root, string sessionRoot)
        {
            File.WriteAllText(Path.Combine(root, "LATEST.txt"), sessionRoot + Environment.NewLine, Encoding.UTF8);
        }

        private static StreamWriter Csv(string path)
        {
            return new StreamWriter(path, false, new UTF8Encoding(true));
        }

        private static void WriteRow(StreamWriter writer, params object[] values)
        {
            writer.WriteLine(string.Join(",", values.Select(CsvValue)));
        }

        private static string CsvValue(object value)
        {
            if (value == null) return "";
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
                return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
