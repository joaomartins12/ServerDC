using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Util
{
    /// <summary>
    /// Writes a stable JSON lookup for runtime TableIndex -> item metadata.
    /// Intended for protocol research, tooling and future administration utilities.
    /// </summary>
    public static class ItemCatalogJsonExporter
    {
        public static void Export(IList<BasicItem> items)
        {
            try
            {
                var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Catalogs");
                Directory.CreateDirectory(root);

                var path = Path.Combine(root, "ItemCatalog.json");
                using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine("{");
                    writer.WriteLine("  \"generatedAtUtc\": \"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\",");
                    writer.WriteLine("  \"count\": " + (items == null ? 0 : items.Count) + ",");
                    writer.WriteLine("  \"items\": [");

                    if (items != null)
                    {
                        for (var i = 0; i < items.Count; i++)
                        {
                            var item = items[i];
                            if (item == null) continue;

                            var part = item as ItemTable.Item;
                            var use = item as UseItemTable.UseItem;
                            var vehicleKey = IsVehicleKey(item);

                            writer.WriteLine("    {");
                            WriteNumber(writer, "tableIndex", i, true);
                            WriteString(writer, "sourceType", part != null ? "Item" : use != null ? "UseItem" : "BasicItem", true);
                            WriteString(writer, "id", item.Id, true);
                            WriteString(writer, "name", item.Name, true);
                            WriteString(writer, "category", item.Category, true);
                            WriteString(writer, "description", item.Description, true);
                            WriteString(writer, "function", item.Function, true);
                            WriteString(writer, "nextState", item.NextState, true);
                            WriteString(writer, "buyValue", item.BuyValue, true);
                            WriteString(writer, "sellValue", item.SellValue, true);
                            WriteNullableInt(writer, "buyPrice", item.BuyValue, true);
                            WriteNullableInt(writer, "sellPrice", item.SellValue, true);
                            WriteString(writer, "expirationTime", item.ExpirationTime, true);
                            WriteNullableBool(writer, "auctionable", item.Auctionable, true);
                            WriteNullableBool(writer, "partsShop", item.PartsShop, true);
                            WriteNullableBool(writer, "sendable", item.Sendable, true);
                            WriteBool(writer, "stackable", !vehicleKey && item.IsStackable(), true);
                            WriteSafeMaxStack(writer, item, vehicleKey, true);
                            WriteString(writer, "grade", part == null ? null : part.Grade, true);
                            WriteString(writer, "requiredLevel", part == null ? null : part.RequiredLevel, true);
                            WriteString(writer, "basePoints", part == null ? null : part.BasePoints, true);
                            WriteString(writer, "basePointModifier", part == null ? null : part.BasePointModifier, true);
                            WriteString(writer, "basePointVariable", part == null ? null : part.BasePointVariable, true);
                            WriteString(writer, "partAssist", part == null ? null : part.PartAssist, true);
                            WriteString(writer, "lube", part == null ? null : part.Lube, true);
                            WriteString(writer, "neoStats", part == null ? null : part.NeoStats, true);
                            WriteString(writer, "stat", use == null ? null : use.StatModifier, true);
                            WriteString(writer, "cooldown", use == null ? null : use.CooldownTime, true);
                            WriteString(writer, "duration", use == null ? null : use.Duration, false);
                            writer.Write("    }");

                            var hasAnother = false;
                            for (var next = i + 1; next < items.Count; next++)
                            {
                                if (items[next] != null)
                                {
                                    hasAnother = true;
                                    break;
                                }
                            }
                            writer.WriteLine(hasAnother ? "," : string.Empty);
                        }
                    }

                    writer.WriteLine("  ]");
                    writer.WriteLine("}");
                }

                Log.Info("Item JSON catalog exported to {0}", path);
            }
            catch (Exception ex)
            {
                Log.Warning("Unable to export ItemCatalog.json: {0}", ex.Message);
            }
        }

        private static bool IsVehicleKey(BasicItem item)
        {
            if (item == null) return false;
            if (!string.Equals((item.Category ?? string.Empty).Trim(), "car", StringComparison.OrdinalIgnoreCase))
                return false;
            return (item.Name ?? string.Empty).Trim().EndsWith("key", StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteString(StreamWriter writer, string name, string value, bool comma)
        {
            writer.Write("      \"");
            writer.Write(Escape(name));
            writer.Write("\": ");
            if (value == null)
                writer.Write("null");
            else
                writer.Write("\"" + Escape(value) + "\"");
            writer.WriteLine(comma ? "," : string.Empty);
        }

        private static void WriteNumber(StreamWriter writer, string name, int value, bool comma)
        {
            writer.WriteLine("      \"" + Escape(name) + "\": " + value.ToString(CultureInfo.InvariantCulture) + (comma ? "," : string.Empty));
        }

        private static void WriteBool(StreamWriter writer, string name, bool value, bool comma)
        {
            writer.WriteLine("      \"" + Escape(name) + "\": " + (value ? "true" : "false") + (comma ? "," : string.Empty));
        }

        private static void WriteNullableInt(StreamWriter writer, string name, string raw, bool comma)
        {
            int value;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                writer.WriteLine("      \"" + Escape(name) + "\": " + value.ToString(CultureInfo.InvariantCulture) + (comma ? "," : string.Empty));
            else
                writer.WriteLine("      \"" + Escape(name) + "\": null" + (comma ? "," : string.Empty));
        }

        private static void WriteNullableBool(StreamWriter writer, string name, string raw, bool comma)
        {
            bool value;
            if (bool.TryParse(raw, out value))
                WriteBool(writer, name, value, comma);
            else if (raw == "1")
                WriteBool(writer, name, true, comma);
            else if (raw == "0")
                WriteBool(writer, name, false, comma);
            else
                writer.WriteLine("      \"" + Escape(name) + "\": null" + (comma ? "," : string.Empty));
        }

        private static void WriteSafeMaxStack(StreamWriter writer, BasicItem item, bool vehicleKey, bool comma)
        {
            if (vehicleKey)
            {
                writer.WriteLine("      \"maxStack\": 1" + (comma ? "," : string.Empty));
                return;
            }

            try
            {
                writer.WriteLine("      \"maxStack\": " + item.GetMaxStack().ToString(CultureInfo.InvariantCulture) + (comma ? "," : string.Empty));
            }
            catch
            {
                writer.WriteLine("      \"maxStack\": null" + (comma ? "," : string.Empty));
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

            var sb = new StringBuilder(value.Length + 16);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
