using System;
using System.Collections.Generic;
using System.Globalization;
using Shared;
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
        public static EquippedItemStats Resolve(Character character, Vehicle vehicle)
        {
            var result = new EquippedItemStats();
            if (character == null || vehicle == null || character.InventoryItems == null || ServerMain.Items == null)
                return result;

            foreach (var inventoryItem in character.InventoryItems)
            {
                if (inventoryItem == null || inventoryItem.State != 1 || inventoryItem.CarId != vehicle.CarId)
                    continue;
                if (inventoryItem.TableIndex < 0 || inventoryItem.TableIndex >= ServerMain.Items.Count)
                    continue;

                var definition = ServerMain.Items[inventoryItem.TableIndex] as ItemTable.Item;
                if (definition == null)
                    continue;

                int basePoints;
                if (!int.TryParse(definition.BasePoints, NumberStyles.Integer, CultureInfo.InvariantCulture, out basePoints))
                    basePoints = 0;

                // UpgradePoint is already persisted per inventory instance and represents the
                // extra stat points gained by upgrading the part. BasePoints is the item's
                // native contribution from Items.xml.
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

                Log.Debug(
                    "Equipped part stats: InvenIdx={0} TableIndex={1} Id={2} Name={3} Category={4} BasePoints={5} UpgradePoint={6} Applied={7}",
                    inventoryItem.InventoryIndex,
                    inventoryItem.TableIndex,
                    definition.Id,
                    definition.Name,
                    category,
                    basePoints,
                    inventoryItem.UpgradePoint,
                    points);
            }

            return result;
        }
    }
}
