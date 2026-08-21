using System;
using System.Collections.Generic;
using System.Globalization;
using Shared;
using Shared.Models;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    internal sealed class EquippedItemStats
    {
        public int Speed;
        public int Crash;
        public int Accel;
        public int Boost;
    }

    internal static class EquippedItemStatResolver
    {
        private static readonly object CacheLock = new object();
        private static Dictionary<int, ItemTable.Item> _clientTableIndexToDefinition;

        public static EquippedItemStats Resolve(Character character, Vehicle vehicle)
        {
            var result = new EquippedItemStats();
            if (character == null || vehicle == null)
                return result;

            // Normal part-shop equipment.
            if (character.InventoryItems != null && ServerMain.Items != null)
            {
                EnsureClientItemCache();

                foreach (var inventoryItem in character.InventoryItems)
                {
                    if (inventoryItem == null || inventoryItem.State != 1 || inventoryItem.CarId != vehicle.CarId)
                        continue;

                    ItemTable.Item definition;
                    if (_clientTableIndexToDefinition == null ||
                        !_clientTableIndexToDefinition.TryGetValue(inventoryItem.TableIndex, out definition) ||
                        definition == null)
                    {
                        definition = inventoryItem.TableIndex >= 0 && inventoryItem.TableIndex < ServerMain.Items.Count
                            ? ServerMain.Items[inventoryItem.TableIndex] as ItemTable.Item
                            : null;

                        if (definition != null)
                        {
                            Log.Warning(
                                "Equipped part fallback mapping used: ClientTableIndex={0} -> ServerItemId={1}. Import ItemClient.tdf to dbo.client_item_lookup for authoritative mapping.",
                                inventoryItem.TableIndex,
                                definition.Id);
                        }
                    }

                    if (definition == null)
                        continue;

                    int basePoints;
                    if (!int.TryParse(definition.BasePoints, NumberStyles.Integer, CultureInfo.InvariantCulture, out basePoints))
                        basePoints = 0;

                    var points = basePoints + Math.Max(0, inventoryItem.UpgradePoint);
                    var category = (definition.Category ?? string.Empty).Trim().ToLowerInvariant();

                    switch (category)
                    {
                        case "speed":
                            result.Speed += points;
                            break;
                        case "crash":
                        case "durability":
                            result.Crash += points;
                            break;
                        case "accel":
                        case "acceleration":
                            result.Accel += points;
                            break;
                        case "boost":
                        case "booster":
                            result.Boost += points;
                            break;
                    }

                    QuietLog.Write(
                        "PartStats",
                        "InvenIdx={0} ClientTableIndex={1} ResolvedItemId={2} Name={3} Category={4} BasePoints={5} UpgradePoint={6} Applied={7}",
                        inventoryItem.InventoryIndex,
                        inventoryItem.TableIndex,
                        definition.Id,
                        definition.Name,
                        category,
                        basePoints,
                        inventoryItem.UpgradePoint,
                        points);
                }
            }

            // Visual-shop items contribute the official Bonus Speed/Accel/Boost/Crash
            // values imported from VShop (or the ServerBonus* overrides in SQL Server).
            try
            {
                using (var conn = GameServer.Instance.Database.Connection)
                {
                    var visual = VisualShopDatabase.LoadEquippedStatBonus(conn, character.Id, vehicle.CarId);
                    result.Speed += visual.Speed;
                    result.Crash += visual.Crash;
                    result.Accel += visual.Accel;
                    result.Boost += visual.Boost;

                    if (visual.Speed != 0 || visual.Crash != 0 || visual.Accel != 0 || visual.Boost != 0)
                    {
                        QuietLog.Write(
                            "PartStats",
                            "VisualShop CarId={0} Bonus[S={1},C={2},A={3},B={4}]",
                            vehicle.CarId, visual.Speed, visual.Crash, visual.Accel, visual.Boost);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Visual shop stat bonus lookup failed for CID={0} CarId={1}: {2}", character.Id, vehicle.CarId, ex.Message);
            }

            return result;
        }

        private static void EnsureClientItemCache()
        {
            if (_clientTableIndexToDefinition != null)
                return;

            lock (CacheLock)
            {
                if (_clientTableIndexToDefinition != null)
                    return;

                var map = new Dictionary<int, ItemTable.Item>();
                try
                {
                    var itemById = new Dictionary<string, ItemTable.Item>(StringComparer.OrdinalIgnoreCase);
                    foreach (var basicItem in ServerMain.Items)
                    {
                        var part = basicItem as ItemTable.Item;
                        if (part == null || string.IsNullOrWhiteSpace(part.Id))
                            continue;
                        itemById[part.Id] = part;
                    }

                    using (var connection = GameServer.Instance.Database.Connection)
                    using (var cmd = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.client_item_lookup', N'U') IS NOT NULL
BEGIN
    SELECT ClientTableIndex, ItemId
    FROM dbo.client_item_lookup
    WHERE ItemId IS NOT NULL;
END", connection))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var clientTableIndex = Convert.ToInt32(reader[0], CultureInfo.InvariantCulture);
                            var itemId = Convert.ToString(reader[1], CultureInfo.InvariantCulture);
                            ItemTable.Item definition;
                            if (!string.IsNullOrWhiteSpace(itemId) && itemById.TryGetValue(itemId, out definition))
                                map[clientTableIndex] = definition;
                        }
                    }

                    QuietLog.Write("PartStats", "Client item stat mapping loaded: {0} indexes resolved to server definitions.", map.Count);
                }
                catch (Exception ex)
                {
                    Log.Warning("Client item stat mapping unavailable: {0}", ex.Message);
                }

                _clientTableIndexToDefinition = map;
            }
        }
    }
}
