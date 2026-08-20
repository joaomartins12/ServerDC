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
        public static void Handle(Packet packet) // TODO: Send data corresponding to the charname, not user
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
            var statisticInfo = BuildStatisticInfo(character.ActiveCar);

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
                if (resolved != null)
                {
                    Log.Info(
                        "GameCharInfo stats: CID={0} CarDbId={1} VehicleId={2} Name={3} Grade=V{4} Source={5} Base[S={6},C={7},A={8},B={9}]",
                        character.Id,
                        character.ActiveCar.CarId,
                        resolved.VehicleId,
                        resolved.VehicleName ?? "UNKNOWN",
                        resolved.Grade,
                        resolved.Source ?? "UNKNOWN",
                        resolved.Speed,
                        resolved.Crash,
                        resolved.Accel,
                        resolved.Boost);
                }
            }

            packet.Sender.Send(ack.CreatePacket());
        }

        private static XiStrStatInfo BuildStatisticInfo(Vehicle vehicle)
        {
            var info = new XiStrStatInfo();
            var resolved = VehicleStatResolver.Resolve(vehicle);
            if (resolved == null)
                return info;

            info.BasedSpeed = resolved.Speed;
            info.BasedCrash = resolved.Crash;
            info.BasedAccel = resolved.Accel;
            info.BasedBoost = resolved.Boost;

            info.TotalSpeed = resolved.Speed;
            info.TotalCrash = resolved.Crash;
            info.TotalAccel = resolved.Accel;
            info.TotalBoost = resolved.Boost;
            return info;
        }
    }
}
