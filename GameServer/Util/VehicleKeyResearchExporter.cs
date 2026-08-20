using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    public static class VehicleKeyResearchExporter
    {
        private sealed class KeyRow
        {
            public int UseItemIndex;
            public string Id;
            public string Name;
            public string RawMaxStack;
        }

        public static void Export(IList<VehicleList.VehicleData> vehicles, IList<BasicItem> combinedItems)
        {
            try
            {
                var useItemsPath = Path.Combine("system", "data", "UseItems.xml");
                if (!File.Exists(useItemsPath) || vehicles == null)
                    return;

                UseItemTable table;
                var serializer = new XmlSerializer(typeof(UseItemTable));
                using (var reader = new StreamReader(useItemsPath))
                    table = (UseItemTable)serializer.Deserialize(reader);

                if (table == null || table.UseItemList == null)
                    return;

                var keys = new List<KeyRow>();
                for (var i = 0; i < table.UseItemList.Count; i++)
                {
                    var item = table.UseItemList[i];
                    if (item == null) continue;
                    if (!string.Equals((item.Category ?? string.Empty).Trim(), "car", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(item.Name ?? string.Empty).Trim().EndsWith("key", StringComparison.OrdinalIgnoreCase)) continue;

                    keys.Add(new KeyRow
                    {
                        UseItemIndex = i,
                        Id = item.Id,
                        Name = item.Name,
                        RawMaxStack = item.MaxStack
                    });
                }

                var firstUseItemCombinedIndex = combinedItems == null ? -1 : combinedItems.Count - table.UseItemList.Count;
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Catalogs");
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "VehicleKeyResearch.csv");

                using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine("VehicleRuntimeIndex,CarType,VehicleName,VehicleGradeIndex,MatchByRawMaxStackUseItemIndex,MatchByRawMaxStackCombinedIndex,MatchByRawMaxStackItemId,MatchByRawMaxStackName,OrdinalUseItemIndex,OrdinalCombinedIndex,OrdinalItemId,OrdinalName,RawMaxStack");

                    for (var runtimeIndex = 0; runtimeIndex < vehicles.Count; runtimeIndex++)
                    {
                        var vehicle = vehicles[runtimeIndex];
                        if (vehicle == null) continue;

                        uint carType;
                        if (!uint.TryParse(vehicle.UniqueId, NumberStyles.Integer, CultureInfo.InvariantCulture, out carType))
                            carType = 0;

                        KeyRow maxStackMatch = null;
                        foreach (var key in keys)
                        {
                            uint raw;
                            if (uint.TryParse(key.RawMaxStack, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw) && raw == carType)
                            {
                                maxStackMatch = key;
                                break;
                            }
                        }

                        var ordinal = runtimeIndex < keys.Count ? keys[runtimeIndex] : null;
                        writer.WriteLine(string.Join(",", new[]
                        {
                            Csv(runtimeIndex),
                            Csv(carType),
                            Csv(vehicle.Name),
                            Csv(vehicle.Grade),
                            Csv(maxStackMatch == null ? (object)null : maxStackMatch.UseItemIndex),
                            Csv(maxStackMatch == null || firstUseItemCombinedIndex < 0 ? (object)null : firstUseItemCombinedIndex + maxStackMatch.UseItemIndex),
                            Csv(maxStackMatch == null ? null : maxStackMatch.Id),
                            Csv(maxStackMatch == null ? null : maxStackMatch.Name),
                            Csv(ordinal == null ? (object)null : ordinal.UseItemIndex),
                            Csv(ordinal == null || firstUseItemCombinedIndex < 0 ? (object)null : firstUseItemCombinedIndex + ordinal.UseItemIndex),
                            Csv(ordinal == null ? null : ordinal.Id),
                            Csv(ordinal == null ? null : ordinal.Name),
                            Csv(maxStackMatch == null ? null : maxStackMatch.RawMaxStack)
                        }));
                    }
                }

                Log.Info("Vehicle/key research mapping exported: {0} (Vehicles={1}, Keys={2}, FirstUseItemCombinedIndex={3})",
                    path, vehicles.Count, keys.Count, firstUseItemCombinedIndex);
            }
            catch (Exception ex)
            {
                Log.Warning("Unable to export VehicleKeyResearch.csv: {0}", ex.Message);
            }
        }

        public static void LogCandidates(uint carType, string vehicleName)
        {
            try
            {
                var useItemsPath = Path.Combine("system", "data", "UseItems.xml");
                if (!File.Exists(useItemsPath)) return;

                UseItemTable table;
                var serializer = new XmlSerializer(typeof(UseItemTable));
                using (var reader = new StreamReader(useItemsPath))
                    table = (UseItemTable)serializer.Deserialize(reader);
                if (table == null || table.UseItemList == null) return;

                Log.Info("VehicleKeyResearch BUY: CarType={0} Vehicle='{1}'", carType, vehicleName);
                for (var i = 0; i < table.UseItemList.Count; i++)
                {
                    var item = table.UseItemList[i];
                    if (item == null || !string.Equals((item.Category ?? string.Empty).Trim(), "car", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(item.Name ?? string.Empty).Trim().EndsWith("key", StringComparison.OrdinalIgnoreCase)) continue;

                    uint raw;
                    if (uint.TryParse(item.MaxStack, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw) && raw == carType)
                    {
                        Log.Info("VehicleKeyResearch CANDIDATE: CarType={0} UseItemIndex={1} ItemId={2} Name={3} RawMaxStack={4}",
                            carType, i, item.Id, item.Name, item.MaxStack);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("VehicleKeyResearch candidate logging failed: {0}", ex.Message);
            }
        }

        private static string Csv(object value)
        {
            if (value == null) return string.Empty;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
