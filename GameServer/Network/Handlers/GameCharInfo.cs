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

            // Prefer the live GameServer session when the requested character is online.
            // That keeps ActiveCar, inventory/equipment and the current vehicle serial in sync
            // with what the client is actually using. DB retrieval remains a fallback for offline
            // profiles.
            var targetClient = GameServer.Instance.Server.GetClient(gameCharInfoPacket.CharacterName);
            Character character;
            User user;
            var source = "db";

            if (targetClient != null && targetClient.User != null && targetClient.User.ActiveCharacter != null)
            {
                character = targetClient.User.ActiveCharacter;
                user = targetClient.User;
                source = "live";

                // Older sessions can reach GameCharInfo before CmdItemList has populated the
                // in-memory inventory. Load it once so equipped-part stats and profile data are
                // based on the same state as the normal inventory flow.
                if (character.InventoryItems == null)
                    character.InventoryItems = new System.Collections.Generic.List<InventoryItem>();

                if (character.InventoryItems.Count == 0)
                    ItemModel.RetrieveAll(GameServer.Instance.Database.Connection, ref character);

                if (character.ActiveCar == null && character.GarageVehicles != null)
                    character.ActiveCar = character.GarageVehicles.Find(v => v != null && v.CarId == character.ActiveVehicleId);
            }
            else
            {
                character = CharacterModel.Retrieve(GameServer.Instance.Database.Connection,
                    gameCharInfoPacket.CharacterName);
                if (character == null)
                {
                    Log.Error("Character {0} was not found in DB!", gameCharInfoPacket.CharacterName);
                    packet.Sender.SendDebugError("Character not found");
#if !DEBUG
                    packet.Sender.KillConnection("Character for CmdGameCharInfo not found");
#endif
                    return;
                }

                ItemModel.RetrieveAll(GameServer.Instance.Database.Connection, ref character);
                user = AccountModel.Retrieve(GameServer.Instance.Database.Connection, character.Uid);
            }

            if (user == null)
            {
                Log.Error("GameCharInfo: account for character {0} was not found.", character.Name);
                packet.Sender.SendDebugError("Account not found");
                return;
            }

            var statisticInfo = BuildStatisticInfo(character);

            var ack = new GameCharInfoAnswer
            {
                Character = character,
                Vehicle = character.ActiveCar ?? new Vehicle(),
                StatisticInfo = statisticInfo,
                Crew = character.Crew,
                Serial = user.VehicleSerial,
                ChId = (char)character.LastChannel
            };

            if (character.ActiveCar != null)
            {
                var resolved = VehicleStatResolver.Resolve(character.ActiveCar);
                var equipped = EquippedItemStatResolver.Resolve(character, character.ActiveCar);
                if (resolved != null)
                {
                    Log.Info(
                        "GameCharInfo source={0}: CID={1} Serial={2} ActiveVehicleId={3} CarDbId={4} CarType={5} VehicleId={6} Name={7} Grade=V{8} Inventory={9} Source={10} Base[S={11},C={12},A={13},B={14}] Equip[S={15},C={16},A={17},B={18}]",
                        source,
                        character.Id,
                        user.VehicleSerial,
                        character.ActiveVehicleId,
                        character.ActiveCar.CarId,
                        character.ActiveCar.CarType,
                        resolved.VehicleId,
                        resolved.VehicleName ?? "UNKNOWN",
                        resolved.Grade,
                        character.InventoryItems == null ? 0 : character.InventoryItems.Count,
                        resolved.Source ?? "UNKNOWN",
                        resolved.Speed,
                        resolved.Crash,
                        resolved.Accel,
                        resolved.Boost,
                        equipped.Speed,
                        equipped.Crash,
                        equipped.Accel,
                        equipped.Boost);
                }
            }
            else
            {
                Log.Warning("GameCharInfo source={0}: CID={1} Serial={2} has no ActiveCar (ActiveVehicleId={3}).",
                    source, character.Id, user.VehicleSerial, character.ActiveVehicleId);
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
            var userBonus = (int)character.Level;

            info.BasedSpeed = resolved.Speed;
            info.BasedCrash = resolved.Crash;
            info.BasedAccel = resolved.Accel;
            info.BasedBoost = resolved.Boost;

            info.EquipSpeed = equipped.Speed;
            info.EquipCrash = equipped.Crash;
            info.EquipAccel = equipped.Accel;
            info.EquipBoost = equipped.Boost;

            info.CharSpeed = userBonus;
            info.CharCrash = userBonus;
            info.CharAccel = userBonus;
            info.CharBoost = userBonus;

            info.TotalSpeed = resolved.Speed + equipped.Speed + userBonus;
            info.TotalCrash = resolved.Crash + equipped.Crash + userBonus;
            info.TotalAccel = resolved.Accel + equipped.Accel + userBonus;
            info.TotalBoost = resolved.Boost + equipped.Boost + userBonus;
            return info;
        }
    }
}
