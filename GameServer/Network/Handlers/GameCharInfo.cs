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

            var targetClient = GameServer.Instance.Server.GetClient(gameCharInfoPacket.CharacterName);
            var character = targetClient?.User?.ActiveCharacter;
            var source = "live";

            if (character == null)
            {
                source = "db";
                character = CharacterModel.Retrieve(
                    GameServer.Instance.Database.Connection,
                    gameCharInfoPacket.CharacterName);
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

            var currentLicenseId = CharacterProgressModel.LoadPersistentStats(
                GameServer.Instance.Database.Connection,
                character);

            if (currentLicenseId <= 0 ||
                !CharacterProgressModel.HasLicense(GameServer.Instance.Database.Connection,
                    character.Id, currentLicenseId))
            {
                CharacterModel.EnsureDefaultLicense(GameServer.Instance.Database.Connection, character.Id);
                currentLicenseId = CharacterProgressModel.GetCurrentLicense(
                    GameServer.Instance.Database.Connection, character.Id);
            }

            if (character.InventoryItems == null || character.InventoryItems.Count == 0)
                ItemModel.RetrieveAll(GameServer.Instance.Database.Connection, ref character);

            var user = targetClient?.User ??
                       AccountModel.Retrieve(GameServer.Instance.Database.Connection, character.Uid);
            var statisticInfo = BuildStatisticInfo(character);
            var serial = user == null ? (ushort)0 : user.VehicleSerial;

            uint profileContextId = serial;
            if (profileContextId == 0)
                profileContextId = (uint)(character.Id & uint.MaxValue);
            if (profileContextId == 0)
                profileContextId = 1;

            Log.Debug(
                "GameCharInfo profile: target={0} Serial={1} ProfileContext={2} License={3} Mileage={4:0.##} PvP={5}W/{6}L Team={7}W/{8}L CarType={9}",
                character.Name,
                serial,
                profileContextId,
                currentLicenseId,
                character.TotalDistance,
                character.PvpWinCount,
                character.PvpCount >= character.PvpWinCount ? character.PvpCount - character.PvpWinCount : 0,
                character.TeamPvpWinCount,
                character.TeamPvpCount >= character.TeamPvpWinCount ? character.TeamPvpCount - character.TeamPvpWinCount : 0,
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
                var resolved = VehicleStatResolver.Resolve(character.ActiveCar);
                var equipped = EquippedItemStatResolver.Resolve(character, character.ActiveCar);
                if (resolved != null)
                {
                    Log.Info(
                        "GameCharInfo source={0}: CID={1} Serial={2} ProfileContext={3} License={4} ActiveVehicleId={5} CarDbId={6} CarType={7} VehicleId={8} Name={9} Grade=V{10} Inventory={11} Source={12} Base[S={13},C={14},A={15},B={16}] Equip[S={17},C={18},A={19},B={20}]",
                        source, character.Id, serial, profileContextId, currentLicenseId,
                        character.ActiveVehicleId, character.ActiveCar.CarId, character.ActiveCar.CarType,
                        resolved.VehicleId, resolved.VehicleName ?? "UNKNOWN", resolved.Grade,
                        character.InventoryItems == null ? 0 : character.InventoryItems.Count,
                        resolved.Source ?? "UNKNOWN", resolved.Speed, resolved.Crash, resolved.Accel, resolved.Boost,
                        equipped.Speed, equipped.Crash, equipped.Accel, equipped.Boost);
                }
            }

            packet.Sender.Send(ack.CreatePacket());
        }

        private static XiStrStatInfo BuildStatisticInfo(Character character)
        {
            var info = new XiStrStatInfo();
            if (character == null || character.ActiveCar == null)
                return info;

            var resolved = VehicleStatResolver.Resolve(character.ActiveCar);
            if (resolved == null)
                return info;

            var equipped = EquippedItemStatResolver.Resolve(character, character.ActiveCar);
            var levelBonus = character.Level > 0 ? character.Level - 1 : 0;

            info.BasedSpeed = resolved.Speed;
            info.BasedCrash = resolved.Crash;
            info.BasedAccel = resolved.Accel;
            info.BasedBoost = resolved.Boost;
            info.CharSpeed = levelBonus;
            info.CharCrash = levelBonus;
            info.CharAccel = levelBonus;
            info.CharBoost = levelBonus;
            info.EquipSpeed = equipped.Speed;
            info.EquipCrash = equipped.Crash;
            info.EquipAccel = equipped.Accel;
            info.EquipBoost = equipped.Boost;
            info.TotalSpeed = resolved.Speed + equipped.Speed + levelBonus;
            info.TotalCrash = resolved.Crash + equipped.Crash + levelBonus;
            info.TotalAccel = resolved.Accel + equipped.Accel + levelBonus;
            info.TotalBoost = resolved.Boost + equipped.Boost + levelBonus;
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
            var ack = new Packet((ushort)1351);
            ack.Writer.Write(0);
            packet.Sender.Send(ack);

            var character = packet.Sender.User?.ActiveCharacter;
            Log.Debug(
                "MyStickerListAck: CID={0} Name={1} Count=0",
                character == null ? 0UL : character.Id,
                character == null ? "UNKNOWN" : character.Name);
        }
    }
}

namespace GameServer.Util
{
    public static class PlayerVisualSnapshotBuilder
    {
        public static XiPlayerInfo BuildPlayerInfo(ushort serial, Character character)
        {
            return new XiPlayerInfo(serial, character)
            {
                Age = 0,
                VisualItem = BuildVisualItem(character),
                UseTime = 0.0f
            };
        }

        public static RoomNotifyChangeAnswer BuildRoomNotifyChange(ushort serial, Character character)
        {
            return new RoomNotifyChangeAnswer
            {
                Serial = serial,
                Age = 0,
                CarAttr = BuildCarAttr(character == null ? null : character.ActiveCar),
                PlayerInfo = BuildPlayerInfo(serial, character)
            };
        }

        public static XiCarAttr BuildCarAttr(Vehicle vehicle)
        {
            var result = new XiCarAttr();
            if (vehicle == null)
                return result;

            const ushort playerCarSort = 0;
            var body = unchecked((ushort)vehicle.CarType);
            var color = vehicle.Color != 0 ? vehicle.Color : vehicle.BaseColor;
            var packed = (ulong)playerCarSort |
                         ((ulong)body << 16) |
                         ((ulong)color << 32);

            result.___u0.__s0.Sort = playerCarSort;
            result.___u0.__s0.Body = body;
            result.___u0.__s1.lvalSortBody = unchecked((int)(uint)packed);
            result.___u0.__s1.lvalColor = unchecked((int)color);
            result.___u0.llval = unchecked((long)packed);
            return result;
        }

        private static XiVisualItem BuildVisualItem(Character character)
        {
            var visual = new XiVisualItem { PlateString = string.Empty };
            if (character == null || character.ActiveCar == null || GameServer.Instance.Database == null)
                return visual;

            try
            {
                using (var conn = GameServer.Instance.Database.Connection)
                using (var cmd = new MySqlCommand(@"
SELECT v.ShopId, v.CategoryIndex, c.Category, v.Data
FROM dbo.visual_items v
JOIN dbo.visual_item_catalog c ON c.ShopId=v.ShopId
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
                            var data = r.IsDBNull(3) ? string.Empty : System.Convert.ToString(r[3]);
                            ApplyVisual(visual, shopId, categoryIndex, category, data);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning("Visual snapshot lookup failed for CID={0}: {1}", character.Id, ex.Message);
            }

            return visual;
        }

        private static void ApplyVisual(XiVisualItem visual, int shopId, int categoryIndex, string category, string data)
        {
            var value = unchecked((short)shopId);
            var normalized = (category ?? string.Empty).Trim().ToLowerInvariant()
                .Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

            if (normalized.Contains("decalcolor") || normalized.Contains("stickercolor"))
                visual.DecalColor = value;
            else if (normalized.Contains("neon"))
                visual.Neon = value;
            else if (normalized.Contains("plate"))
            {
                visual.Plate = value;
                visual.PlateString = string.IsNullOrEmpty(data) ? string.Empty : data;
            }
            else if (normalized.Contains("decal") || normalized.Contains("sticker"))
                visual.Decal = value;
            else if (normalized.Contains("bumper"))
                visual.AeroBumper = value;
            else if (normalized.Contains("intercooler"))
                visual.AeroIntercooler = value;
            else if (normalized.Contains("aero") || normalized.Contains("bodykit") || normalized.Contains("bodyset"))
                visual.AeroSet = value;
            else if (normalized.Contains("muffler") || normalized.Contains("flame"))
                visual.MufflerFlame = value;
            else if (normalized.Contains("wheel") || normalized.Contains("rim"))
                visual.Wheel = value;
            else if (normalized.Contains("spoiler") || normalized.Contains("wing"))
                visual.Spoiler = value;
            else
                Log.Debug("Visual snapshot: unmapped category ShopId={0} CategoryIndex={1} Category={2}", shopId, categoryIndex, category ?? string.Empty);
        }
    }
}
