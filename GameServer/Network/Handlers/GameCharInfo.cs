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

            var character = CharacterModel.Retrieve(GameServer.Instance.Database.Connection,
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

            var user = AccountModel.Retrieve(GameServer.Instance.Database.Connection, character.Uid);
            var statisticInfo = BuildStatisticInfo(character);

            var ack = new GameCharInfoAnswer
            {
                Character = character,
                Vehicle = character.ActiveCar,
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
                        "GameCharInfo stats: CID={0} CarDbId={1} VehicleId={2} Name={3} Grade=V{4} Source={5} Base[S={6},C={7},A={8},B={9}] Equip[S={10},C={11},A={12},B={13}]",
                        character.Id,
                        character.ActiveCar.CarId,
                        resolved.VehicleId,
                        resolved.VehicleName ?? "UNKNOWN",
                        resolved.Grade,
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

        private static XiStrStatInfo BuildStatisticInfo(Character character)
        {
            var info = new XiStrStatInfo();
            if (character == null || character.ActiveCar == null)
                return info;

            var resolved = VehicleStatResolver.Resolve(character.ActiveCar);
            if (resolved == null)
                return info;

            var equipped = EquippedItemStatResolver.Resolve(character, character.ActiveCar);

            info.BasedSpeed = resolved.Speed;
            info.BasedCrash = resolved.Crash;
            info.BasedAccel = resolved.Accel;
            info.BasedBoost = resolved.Boost;

            info.EquipSpeed = equipped.Speed;
            info.EquipCrash = equipped.Crash;
            info.EquipAccel = equipped.Accel;
            info.EquipBoost = equipped.Boost;

            info.TotalSpeed = resolved.Speed + equipped.Speed;
            info.TotalCrash = resolved.Crash + equipped.Crash;
            info.TotalAccel = resolved.Accel + equipped.Accel;
            info.TotalBoost = resolved.Boost + equipped.Boost;
            return info;
        }
    }
}
