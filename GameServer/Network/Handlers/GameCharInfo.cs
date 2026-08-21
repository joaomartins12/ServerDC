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

        public static uint? ResolveVisualPaintColor(Character character)
        {
            return ResolveVisualColor(character, "paint");
        }

        public static uint? ResolveVisualTintColor(Character character)
        {
            return ResolveVisualColor(character, "tint");
        }

        private static uint PackTintRgb565(uint rgb)
        {
            var r = (rgb >> 16) & 0xFFu;
            var g = (rgb >> 8) & 0xFFu;
            var b = rgb & 0xFFu;
            return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        }

        public static void ApplyActivePaint(Character character)
        {
            if (character == null || character.ActiveCar == null || global::GameServer.GameServer.Instance.Database == null)
                return;

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
                Log.Warning("Visual color vehicle persistence failed: CID={0} CarId={1} Error={2}",
                    character.Id, character.ActiveCar.CarId, ex.Message);
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
                            ApplyVisual(visual, shopId, categoryIndex, category, itemCode, data);
                        }
                    }
                }
            }
            catch (System.Exception ex) { Log.Warning("Visual snapshot lookup failed for CID={0}: {1}", character.Id, ex.Message); }
            return visual;
        }

        /// <summary>
        /// Applies VisualItem.xlt's real category dispatcher (sub_54CC80) to the
        /// 0x38-byte XiVisualItem. Categories not present in the retail jump table are
        /// intentionally not forced into a guessed slot. In particular category 3
        /// (window tint in our imported data) and category 32 (paint) are no-ops here;
        /// their RGB values belong to XiStrCarInfo Color2/Color respectively.
        /// </summary>
        private static void ApplyVisual(XiVisualItem visual, int shopId, int categoryIndex, string category, string itemCode, string data)
        {
            var value = unchecked((ushort)shopId);
            uint numericData;
            var hasNumericData = TryParseVisualData(data, out numericData);

            switch (categoryIndex)
            {
                case 2:
                    visual.Slot00 = value;
                    if (hasNumericData) visual.Value0A = numericData;
                    return;
                case 4:
                    visual.Slot0E = value;
                    return;
                case 5:
                    visual.Slot10 = value;
                    return;
                case 6:
                    visual.Slot12 = value;
                    return;
                case 7:
                    visual.Slot18 = value;
                    return;
                case 8:
                    visual.Slot16 = value;
                    return;
                case 9:
                    visual.Slot02 = value;
                    visual.PlateString = string.IsNullOrEmpty(data) ? string.Empty : data;
                    return;
                case 10:
                    visual.Slot14 = value != 0 ? value : unchecked((ushort)numericData);
                    return;
                case 11:
                    visual.Slot04 = value;
                    if (hasNumericData) visual.Value06 = numericData;
                    return;
                case 47:
                    visual.Slot1A = value;
                    return;
                case 48:
                    visual.Slot16 = value != 0 ? value : unchecked((ushort)numericData);
                    return;
                case 52:
                    visual.Slot1C = value;
                    return;
                case 57:
                    visual.Slot1E = value;
                    return;

                // Explicit retail no-op categories for the two RGB customizations.
                case 1:
                case 3:
                case 32:
                    return;
            }

            Log.Debug("Visual snapshot: retail category has no XiVisualItem slot ShopId={0} CategoryIndex={1} Category={2} ItemCode={3}",
                shopId, categoryIndex, category ?? string.Empty, itemCode ?? string.Empty);
        }

        private static bool TryParseVisualData(string data, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(data)) return false;
            var text = data.Trim();
            if (text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            return uint.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
