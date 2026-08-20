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

            // Prefer the live session. User Information is primarily requested for
            // players currently visible in the world, and the live Character contains
            // the authoritative active vehicle/inventory state for that session.
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

            // DB fallback and older live sessions may not have their inventory loaded yet.
            // Equipment stats and profile data should never depend on opening inventory first.
            if (character.InventoryItems == null || character.InventoryItems.Count == 0)
                ItemModel.RetrieveAll(GameServer.Instance.Database.Connection, ref character);

            var user = targetClient?.User ??
                       AccountModel.Retrieve(GameServer.Instance.Database.Connection, character.Uid);
            var statisticInfo = BuildStatisticInfo(character);

            var serial = user == null ? (ushort)0 : user.VehicleSerial;
            var ack = new GameCharInfoAnswer
            {
                Character = character,
                Vehicle = character.ActiveCar,
                StatisticInfo = statisticInfo,
                Crew = character.Crew,
                Serial = serial,
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
                        serial,
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

            packet.Sender.Send(ack.CreatePacket());
        }

        // The v0.77 client requests this immediately after loading a character. Leaving
        // packet 1350 unanswered leaves its sticker/vehicle-visual subsystem uninitialized.
        // The original server also accompanies remote player information with sticker data.
        // Until persistent sticker records are implemented, explicitly return an empty list
        // instead of silently dropping the request. 1351 is the response paired with 1350.
        [Packet((ushort)1350)]
        public static void MyStickerList(Packet packet)
        {
            var ack = new Packet((ushort)1351);
            ack.Writer.Write(0); // Sticker count.
            packet.Sender.Send(ack);

            var character = packet.Sender.User?.ActiveCharacter;
            Log.Debug(
                "MyStickerListAck: CID={0} Name={1} Count=0",
                character == null ? 0UL : character.Id,
                character == null ? "UNKNOWN" : character.Name);
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
