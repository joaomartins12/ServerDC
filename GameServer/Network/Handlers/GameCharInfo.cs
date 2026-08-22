using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class GameCharInfo
    {
        [Packet(Packets.CmdGameCharInfo)]
        public static void Handle(Packet packet)
        {
            var gameCharInfoPacket = new GameCharInfoPacket(packet);
            var targetClient = global::GameServer.GameServer.Instance.Server.GetClient(gameCharInfoPacket.CharacterName);
            var character = targetClient?.User?.ActiveCharacter;
            var source = "live";

            if (character == null)
            {
                source = "db";
                character = CharacterModel.Retrieve(global::GameServer.GameServer.Instance.Database.Connection, gameCharInfoPacket.CharacterName);
            }
            if (character == null)
            {
                Log.Error("Character {0} was not found in DB or online sessions!", gameCharInfoPacket.CharacterName);
                packet.Sender.SendDebugError("Character not found");
#if !DEBUG
                packet.Sender.KillConnection("Character for CmdGameCharInfo not found");
#endif
                return;
            }

            var currentLicenseId = CharacterProgressModel.LoadPersistentStats(global::GameServer.GameServer.Instance.Database.Connection, character);
            if (currentLicenseId <= 0 || !CharacterProgressModel.HasLicense(global::GameServer.GameServer.Instance.Database.Connection, character.Id, currentLicenseId))
            {
                CharacterModel.EnsureDefaultLicense(global::GameServer.GameServer.Instance.Database.Connection, character.Id);
                currentLicenseId = CharacterProgressModel.GetCurrentLicense(global::GameServer.GameServer.Instance.Database.Connection, character.Id);
            }

            if (character.InventoryItems == null || character.InventoryItems.Count == 0)
                ItemModel.RetrieveAll(global::GameServer.GameServer.Instance.Database.Connection, ref character);

            var user = targetClient?.User ?? AccountModel.Retrieve(global::GameServer.GameServer.Instance.Database.Connection, character.Uid);
            var statisticInfo = BuildStatisticInfo(character);
            var serial = user == null ? (ushort)0 : user.VehicleSerial;
            uint profileContextId = serial;
            if (profileContextId == 0) profileContextId = (uint)(character.Id & uint.MaxValue);
            if (profileContextId == 0) profileContextId = 1;

            Log.Debug("GameCharInfo profile: target={0} Serial={1} ProfileContext={2} License={3} Mileage={4:0.##} PvP={5}W/{6}L Team={7}W/{8}L CarType={9}",
                character.Name, serial, profileContextId, currentLicenseId, character.TotalDistance,
                character.PvpWinCount, character.PvpCount >= character.PvpWinCount ? character.PvpCount - character.PvpWinCount : 0,
                character.TeamPvpWinCount, character.TeamPvpCount >= character.TeamPvpWinCount ? character.TeamPvpCount - character.TeamPvpWinCount : 0,
                character.ActiveCar == null ? 0u : character.ActiveCar.CarType);

            var ack = new GameCharInfoAnswer
            {
                Character = character,
                Vehicle = character.ActiveCar ?? new Vehicle(),
                StatisticInfo = statisticInfo,
                Crew = character.Crew,
                ProfileContextId = profileContextId,
                CurrentLicenseId = currentLicenseId,
                LocType = 2
            };

            if (character.ActiveCar != null)
            {
                PlayerVisualSnapshotBuilder.ApplyActivePaint(character);
                var resolved = VehicleStatResolver.Resolve(character.ActiveCar);
                var equipped = EquippedItemStatResolver.Resolve(character, character.ActiveCar);
                if (resolved != null)
                    Log.Info("GameCharInfo source={0}: CID={1} Serial={2} ProfileContext={3} License={4} ActiveVehicleId={5} CarDbId={6} CarType={7} VehicleId={8} Name={9} Grade=V{10} Inventory={11} Source={12} Base[S={13},C={14},A={15},B={16}] Equip[S={17},C={18},A={19},B={20}]",
                        source, character.Id, serial, profileContextId, currentLicenseId, character.ActiveVehicleId,
                        character.ActiveCar.CarId, character.ActiveCar.CarType, resolved.VehicleId, resolved.VehicleName ?? "UNKNOWN",
                        resolved.Grade, character.InventoryItems == null ? 0 : character.InventoryItems.Count, resolved.Source ?? "UNKNOWN",
                        resolved.Speed, resolved.Crash, resolved.Accel, resolved.Boost, equipped.Speed, equipped.Crash, equipped.Accel, equipped.Boost);
            }

            packet.Sender.Send(ack.CreatePacket());
        }

        private static XiStrStatInfo BuildStatisticInfo(Character character)
        {
            var info = new XiStrStatInfo();
            if (character == null || character.ActiveCar == null) return info;
            var resolved = VehicleStatResolver.Resolve(character.ActiveCar);
            if (resolved == null) return info;
            var equipped = EquippedItemStatResolver.Resolve(character, character.ActiveCar);
            var levelBonus = character.Level > 0 ? character.Level - 1 : 0;
            info.BasedSpeed = resolved.Speed; info.BasedCrash = resolved.Crash; info.BasedAccel = resolved.Accel; info.BasedBoost = resolved.Boost;
            info.CharSpeed = levelBonus; info.CharCrash = levelBonus; info.CharAccel = levelBonus; info.CharBoost = levelBonus;
            info.EquipSpeed = equipped.Speed; info.EquipCrash = equipped.Crash; info.EquipAccel = equipped.Accel; info.EquipBoost = equipped.Boost;
            info.TotalSpeed = resolved.Speed + equipped.Speed + levelBonus; info.TotalCrash = resolved.Crash + equipped.Crash + levelBonus;
            info.TotalAccel = resolved.Accel + equipped.Accel + levelBonus; info.TotalBoost = resolved.Boost + equipped.Boost + levelBonus;
            return info;
        }
    }
}

namespace GameServer.Network.Handlers.Join
{
    public class MyStickerList
    {
        [Packet((ushort)1350)]
        public static void Handle(Packet packet)
        {
            var ack = new Packet((ushort)1351); ack.Writer.Write(0); packet.Sender.Send(ack);
            var character = packet.Sender.User?.ActiveCharacter;
            Log.Debug("MyStickerListAck: CID={0} Name={1} Count=0", character == null ? 0UL : character.Id, character == null ? "UNKNOWN" : character.Name);
        }
    }
}

namespace GameServer.Util
{
    public static class PlayerVisualSnapshotBuilder
    {
        private sealed class VisualDefinition
        {
            public int Id;
            public int CategoryIndex;
            public int Index;
            public string ItemCode = string.Empty;
        }

        private static readonly object VisualIndexSync = new object();
        private static System.Collections.Generic.Dictionary<int, int> _visualIndexByShopId;
        private static System.Collections.Generic.Dictionary<int, VisualDefinition> _visualDefinitionById;
        private static System.Collections.Generic.Dictionary<string, VisualDefinition> _visualDefinitionByItemCode;
        private static System.Collections.Generic.Dictionary<int, string> _vshopItemCodeById;
        private static System.Collections.Generic.Dictionary<string, int> _vshopIdByItemCode;
        private static readonly System.Collections.Generic.HashSet<int> MissingVisualIndexWarnings =
            new System.Collections.Generic.HashSet<int>();

        public static XiPlayerInfo BuildPlayerInfo(ushort serial, Character character)
        {
            return new XiPlayerInfo(serial, character) { Age = 0, VisualItem = BuildVisualItem(character), UseTime = 0.0f };
        }

        public static RoomNotifyChangeAnswer BuildRoomNotifyChange(ushort serial, Character character)
        {
            return new RoomNotifyChangeAnswer
            {
                Serial = serial,
                Age = 0,
                CarAttr = BuildCarAttr(character),
                PlayerInfo = BuildPlayerInfo(serial, character)
            };
        }

        public static XiCarAttr BuildCarAttr(Character character)
        {
            return BuildCarAttr(character == null ? null : character.ActiveCar,
                character == null ? (uint?)null : ResolveVisualPaintColor(character));
        }

        public static XiCarAttr BuildCarAttr(Vehicle vehicle)
        {
            return BuildCarAttr(vehicle, null);
        }

        private static XiCarAttr BuildCarAttr(Vehicle vehicle, uint? visualColor)
        {
            var result = new XiCarAttr();
            if (vehicle == null) return result;
            const ushort playerCarSort = 0;
            var body = unchecked((ushort)vehicle.CarType);
            var color = visualColor ?? (vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor);
            var packed = (ulong)playerCarSort | ((ulong)body << 16) | ((ulong)color << 32);
            result.___u0.__s0.Sort = playerCarSort;
            result.___u0.__s0.Body = body;
            result.___u0.__s1.lvalSortBody = unchecked((int)(uint)packed);
            result.___u0.__s1.lvalColor = unchecked((int)color);
            result.___u0.llval = unchecked((long)packed);
            return result;
        }

        private static uint? ResolveVisualColor(Character character, string kind)
        {
            if (character == null || character.ActiveCar == null || global::GameServer.GameServer.Instance.Database == null) return null;
            try
            {
                var isTint = kind == "tint";
                using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
                using (var cmd = new MySqlCommand(isTint ? @"
SELECT TOP 1 v.Data
FROM dbo.visual_items v
JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
WHERE v.CharacterId=@cid AND v.CarId=@carId AND v.ItemState=1
  AND (v.ExpireTime=0 OR v.ExpireTime>@now)
  AND (v.CategoryIndex=3 OR LOWER(c.ItemCode) LIKE '%window%' OR LOWER(c.Category) LIKE '%windowtint%')
ORDER BY v.InventoryIndex DESC;" : @"
SELECT TOP 1 v.Data
FROM dbo.visual_items v
JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
WHERE v.CharacterId=@cid AND v.CarId=@carId AND v.ItemState=1
  AND (v.ExpireTime=0 OR v.ExpireTime>@now)
  AND (v.CategoryIndex IN (1,32) OR LOWER(c.ItemCode) LIKE '%paint%' OR LOWER(c.Category) LIKE '%paint%')
ORDER BY v.InventoryIndex DESC;", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", character.Id);
                    cmd.Parameters.AddWithValue("@carId", character.ActiveCar.CarId);
                    cmd.Parameters.AddWithValue("@now", System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    var raw = cmd.ExecuteScalar();
                    uint color;
                    if (raw != null && raw != System.DBNull.Value && uint.TryParse(System.Convert.ToString(raw).Trim(), out color))
                    {
                        Log.Debug("Visual {0} resolved: CID={1} CarId={2} Color={3} Hex=0x{4:X6}",
                            isTint ? "tint" : "paint", character.Id, character.ActiveCar.CarId, color, color);
                        return color;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("Visual {0} lookup failed for CID={1}: {2}", kind, character.Id, ex.Message);
            }
            return null;
        }

        public static uint? ResolveVisualPaintColor(Character character) { return ResolveVisualColor(character, "paint"); }
        public static uint? ResolveVisualTintColor(Character character) { return ResolveVisualColor(character, "tint"); }

        private static uint PackTintRgb565(uint rgb)
        {
            var r = (rgb >> 16) & 0xFFu;
            var g = (rgb >> 8) & 0xFFu;
            var b = rgb & 0xFFu;
            return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        }

        public static void ApplyActivePaint(Character character)
        {
            if (character == null || character.ActiveCar == null || global::GameServer.GameServer.Instance.Database == null) return;

            var paint = ResolveVisualPaintColor(character);
            var tintRgb = ResolveVisualTintColor(character);
            var color = paint ?? character.ActiveCar.BaseColor;
            var color2 = tintRgb.HasValue ? PackTintRgb565(tintRgb.Value) : 0u;
            var changed = character.ActiveCar.Color != color || character.ActiveCar.Color2 != color2;

            character.ActiveCar.Color = color;
            character.ActiveCar.Color2 = color2;
            if (character.GarageVehicles != null)
            {
                foreach (var vehicle in character.GarageVehicles)
                {
                    if (vehicle == null || vehicle.CarId != character.ActiveCar.CarId) continue;
                    vehicle.Color = color;
                    vehicle.Color2 = color2;
                }
            }

            if (!changed) return;
            try
            {
                using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
                    VehicleModel.Update(conn, character.ActiveCar);
                Log.Info("Visual colors persisted to vehicle: CID={0} CarId={1} Color={2} TintRGB={3} TintRGB565=0x{4:X4} Paint={5} Tint={6}",
                    character.Id, character.ActiveCar.CarId, color, tintRgb ?? 0u, color2,
                    paint.HasValue ? "visual" : "base", tintRgb.HasValue ? "visual" : "default");
            }
            catch (System.Exception ex)
            {
                Log.Warning("Visual color vehicle persistence failed: CID={0} CarId={1} Error={2}", character.Id, character.ActiveCar.CarId, ex.Message);
            }
        }

        public static XiVisualItem BuildVisualItem(Character character)
        {
            var visual = new XiVisualItem { PlateString = string.Empty };
            if (character == null || character.ActiveCar == null || global::GameServer.GameServer.Instance.Database == null) return visual;
            try
            {
                using (var conn = global::GameServer.GameServer.Instance.Database.Connection)
                using (var cmd = new MySqlCommand(@"
SELECT v.ShopId,v.CategoryIndex,c.Category,c.ItemCode,v.Data
FROM dbo.visual_items v JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
WHERE v.CharacterId=@cid AND v.CarId=@carId AND v.ItemState=1
  AND (v.ExpireTime=0 OR v.ExpireTime>@now)
ORDER BY v.InventoryIndex;", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", character.Id);
                    cmd.Parameters.AddWithValue("@carId", character.ActiveCar.CarId);
                    cmd.Parameters.AddWithValue("@now", System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var shopId = System.Convert.ToInt32(r[0]);
                            var categoryIndex = System.Convert.ToInt32(r[1]);
                            var category = r.IsDBNull(2) ? string.Empty : System.Convert.ToString(r[2]);
                            var itemCode = r.IsDBNull(3) ? string.Empty : System.Convert.ToString(r[3]);
                            var data = r.IsDBNull(4) ? string.Empty : System.Convert.ToString(r[4]);
                            var visualIndex = ResolveVisualIndex(shopId);

                            Log.Debug("Visual snapshot item: ShopId={0} VisualIndex={1} CategoryIndex={2} Category={3} ItemCode={4}",
                                shopId, visualIndex, categoryIndex, category ?? string.Empty, itemCode ?? string.Empty);
                            ApplyVisual(visual, shopId, visualIndex, categoryIndex, category, itemCode, data);
                        }
                    }
                }
            }
            catch (System.Exception ex) { Log.Warning("Visual snapshot lookup failed for CID={0}: {1}", character.Id, ex.Message); }
            return visual;
        }

        private static int ResolveVisualIndex(int shopId)
        {
            EnsureVisualMaps();
            int visualIndex;
            if (_visualIndexByShopId != null && _visualIndexByShopId.TryGetValue(shopId, out visualIndex)) return visualIndex;
            lock (VisualIndexSync)
            {
                if (MissingVisualIndexWarnings.Add(shopId))
                    Log.Warning("Visual render index missing for ShopId={0}; falling back to ShopId. Check Importer/VisualItem.xlt.", shopId);
            }
            return shopId;
        }

        private static void EnsureVisualMaps()
        {
            if (_visualIndexByShopId != null) return;
            lock (VisualIndexSync)
            {
                if (_visualIndexByShopId != null) return;

                var indexMap = new System.Collections.Generic.Dictionary<int, int>();
                var defById = new System.Collections.Generic.Dictionary<int, VisualDefinition>();
                var defByCode = new System.Collections.Generic.Dictionary<string, VisualDefinition>(System.StringComparer.OrdinalIgnoreCase);
                var vshopCodeById = new System.Collections.Generic.Dictionary<int, string>();
                var vshopIdByCode = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
                var importer = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Importer");
                var visualPath = System.IO.Path.Combine(importer, "VisualItem.xlt");
                var vshopPath = System.IO.Path.Combine(importer, "VShopItem.xlt");

                try
                {
                    LoadVisualItemMaps(visualPath, indexMap, defById, defByCode);
                    LoadVShopMaps(vshopPath, vshopCodeById, vshopIdByCode);
                    Log.Info("Visual retail maps loaded: VisualIds={0} VisualCodes={1} VShopIds={2}", defById.Count, defByCode.Count, vshopCodeById.Count);
                }
                catch (System.Exception ex)
                {
                    Log.Warning("Visual retail map load failed: {0}", ex.Message);
                }

                _visualIndexByShopId = indexMap;
                _visualDefinitionById = defById;
                _visualDefinitionByItemCode = defByCode;
                _vshopItemCodeById = vshopCodeById;
                _vshopIdByItemCode = vshopIdByCode;
            }
        }

        private static string[] ReadUnicodeLines(string path)
        {
            if (!System.IO.File.Exists(path)) return new string[0];
            var bytes = System.IO.File.ReadAllBytes(path);
            var offset = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? 2 : 0;
            var text = System.Text.Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset);
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static void LoadVisualItemMaps(string path,
            System.Collections.Generic.Dictionary<int, int> indexMap,
            System.Collections.Generic.Dictionary<int, VisualDefinition> defById,
            System.Collections.Generic.Dictionary<string, VisualDefinition> defByCode)
        {
            var lines = ReadUnicodeLines(path);
            var header = -1; var idCol = -1; var indexCol = -1; var categoryCol = -1; var codeCol = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("Category\tcategory index\tindex\titem_id\tid\t", System.StringComparison.Ordinal)) continue;
                header = i;
                var h = lines[i].Split('\t');
                for (var c = 0; c < h.Length; c++)
                {
                    var name = (h[c] ?? string.Empty).Trim();
                    if (name.Equals("id", System.StringComparison.OrdinalIgnoreCase)) idCol = c;
                    else if (name.Equals("index", System.StringComparison.OrdinalIgnoreCase)) indexCol = c;
                    else if (name.Equals("category index", System.StringComparison.OrdinalIgnoreCase)) categoryCol = c;
                    else if (name.Equals("item_id", System.StringComparison.OrdinalIgnoreCase)) codeCol = c;
                }
                break;
            }
            if (header < 0 || idCol < 0 || indexCol < 0 || categoryCol < 0 || codeCol < 0) return;

            for (var i = header + 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var v = lines[i].Split('\t');
                if (idCol >= v.Length || indexCol >= v.Length || categoryCol >= v.Length || codeCol >= v.Length) continue;
                int id, index, category;
                if (!int.TryParse(v[idCol].Trim().Trim('"'), out id) || !int.TryParse(v[indexCol].Trim().Trim('"'), out index)) continue;
                int.TryParse(v[categoryCol].Trim().Trim('"'), out category);
                var code = v[codeCol].Trim().Trim('"');
                var def = new VisualDefinition { Id = id, Index = index, CategoryIndex = category, ItemCode = code };

                // Retail map insertion keeps the first definition for duplicate dwId.
                if (!defById.ContainsKey(id))
                {
                    defById.Add(id, def);
                    indexMap.Add(id, index);
                }
                if (!string.IsNullOrEmpty(code) && !defByCode.ContainsKey(code)) defByCode.Add(code, def);
            }
        }

        private static void LoadVShopMaps(string path,
            System.Collections.Generic.Dictionary<int, string> codeById,
            System.Collections.Generic.Dictionary<string, int> idByCode)
        {
            var lines = ReadUnicodeLines(path);
            var header = -1; var idCol = -1; var codeCol = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("Index\tSupport\tUniqueId\t", System.StringComparison.Ordinal)) continue;
                header = i;
                var h = lines[i].Split('\t');
                for (var c = 0; c < h.Length; c++)
                {
                    var name = (h[c] ?? string.Empty).Trim();
                    if (name.Equals("UniqueId", System.StringComparison.OrdinalIgnoreCase)) idCol = c;
                    else if (name.Equals("ItemName", System.StringComparison.OrdinalIgnoreCase)) codeCol = c;
                }
                break;
            }
            if (header < 0 || idCol < 0 || codeCol < 0) return;
            for (var i = header + 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var v = lines[i].Split('\t');
                if (idCol >= v.Length || codeCol >= v.Length) continue;
                int id;
                if (!int.TryParse(v[idCol].Trim().Trim('"'), out id)) continue;
                var code = v[codeCol].Trim().Trim('"');
                if (!codeById.ContainsKey(id)) codeById.Add(id, code);
                if (!string.IsNullOrEmpty(code) && !idByCode.ContainsKey(code)) idByCode.Add(code, id);
            }
        }

        private static void ApplyVisual(XiVisualItem visual, int shopId, int visualIndex, int categoryIndex, string category, string itemCode, string data)
        {
            var value = unchecked((ushort)visualIndex);
            uint numericData;
            var hasNumericData = TryParseVisualData(data, out numericData);

            switch (categoryIndex)
            {
                case 2:
                    visual.Slot00 = value;
                    if (hasNumericData) visual.Value0A = numericData;
                    return;
                case 4: visual.Slot0E = value; return;
                case 5: visual.Slot10 = value; return;
                case 6:
                    if (value != 0) { visual.Slot12 = value; return; }
                    if (!TryApplyRetailAeroPackage(visual, data))
                        Log.Warning("Visual Aero package could not be resolved: ShopId={0} Data='{1}'", shopId, data ?? string.Empty);
                    return;
                case 7: visual.Slot18 = value != 0 ? value : unchecked((ushort)numericData); return;
                case 8: visual.Slot16 = value; return;
                case 9:
                    visual.Slot02 = value;
                    visual.PlateString = string.IsNullOrEmpty(data) ? string.Empty : data;
                    return;
                case 10: visual.Slot14 = value != 0 ? value : unchecked((ushort)numericData); return;
                case 11:
                    visual.Slot04 = value;
                    if (hasNumericData) visual.Value06 = numericData;
                    return;
                case 47: visual.Slot1A = value; return;
                case 48: visual.Slot16 = value != 0 ? value : unchecked((ushort)numericData); return;
                case 52: visual.Slot1C = value; return;
                case 57: visual.Slot1E = value; return;
                case 1:
                case 3:
                case 32:
                    return;
            }

            Log.Debug("Visual snapshot: retail category has no XiVisualItem slot ShopId={0} VisualIndex={1} CategoryIndex={2} Category={3} ItemCode={4}",
                shopId, visualIndex, categoryIndex, category ?? string.Empty, itemCode ?? string.Empty);
        }

        private static bool TryApplyRetailAeroPackage(XiVisualItem visual, string data)
        {
            EnsureVisualMaps();
            if (visual == null || string.IsNullOrWhiteSpace(data)) return false;

            // DriftCity.exe v0.77a special case at 0x54E2F4 -> 0x767C60/0x6CE290:
            // pc_Aero (category 6, index 0) uses its 19-wchar parameter. The client
            // reads a three-digit car type, a one-digit set selector and a five-digit
            // VShop UniqueId. The final id identifies a concrete car-specific Aero item
            // (e.g. "          038310859" -> UniqueId 10859, Cielo front Mk.2).
            var text = data.Trim();
            if (text.Length < 5) return false;
            int concreteId;
            if (!int.TryParse(text.Substring(text.Length - 5), out concreteId) || concreteId <= 0) return false;

            var applied = false;
            applied |= TryApplyAeroDefinition(visual, concreteId);

            string code;
            if (_vshopItemCodeById != null && _vshopItemCodeById.TryGetValue(concreteId, out code) && !string.IsNullOrWhiteSpace(code))
            {
                // pc_0033c_b04 / pc_0033c_i04 are the paired front/hood definitions
                // returned by the retail resolver. Expand either side to its companion.
                string companion = null;
                var marker = code.LastIndexOf("_b", System.StringComparison.OrdinalIgnoreCase);
                if (marker >= 0 && marker + 4 == code.Length)
                    companion = code.Substring(0, marker) + "_i" + code.Substring(marker + 2);
                else
                {
                    marker = code.LastIndexOf("_i", System.StringComparison.OrdinalIgnoreCase);
                    if (marker >= 0 && marker + 4 == code.Length)
                        companion = code.Substring(0, marker) + "_b" + code.Substring(marker + 2);
                }

                int companionId;
                if (!string.IsNullOrEmpty(companion) && _vshopIdByItemCode != null && _vshopIdByItemCode.TryGetValue(companion, out companionId))
                    applied |= TryApplyAeroDefinition(visual, companionId);

                Log.Debug("Visual Aero package resolve: Data='{0}' ConcreteId={1} ItemCode={2} Companion={3} Applied={4}",
                    data, concreteId, code, companion ?? string.Empty, applied);
            }
            return applied;
        }

        private static bool TryApplyAeroDefinition(XiVisualItem visual, int id)
        {
            VisualDefinition def;
            if (_visualDefinitionById == null || !_visualDefinitionById.TryGetValue(id, out def) || def == null || def.Index == 0)
                return false;

            var value = unchecked((ushort)def.Index);
            switch (def.CategoryIndex)
            {
                case 4: visual.Slot0E = value; break;
                case 5: visual.Slot10 = value; break;
                case 6: visual.Slot12 = value; break;
                default:
                    Log.Debug("Visual Aero concrete definition ignored: Id={0} ItemCode={1} CategoryIndex={2} Index={3}", id, def.ItemCode, def.CategoryIndex, def.Index);
                    return false;
            }
            Log.Debug("Visual Aero concrete definition: Id={0} ItemCode={1} CategoryIndex={2} Index={3}", id, def.ItemCode, def.CategoryIndex, def.Index);
            return true;
        }

        private static bool TryParseVisualData(string data, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(data)) return false;
            var text = data.Trim();
            if (text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out value);

            if (uint.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                return true;

            // Decal colour parameters are emitted by the retail client as signed
            // decimal int32 values (e.g. -16579837). They are bit patterns, not a
            // negative colour. Preserve those exact 32 bits for XiVisualItem.Value06.
            int signed;
            if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out signed))
            {
                value = unchecked((uint)signed);
                return true;
            }
            return false;
        }
    }
}
