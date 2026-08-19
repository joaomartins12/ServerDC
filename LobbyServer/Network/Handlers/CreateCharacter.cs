using System.Collections.Generic;
using Shared.Models;
using Shared.Network;
using Shared.Network.LobbyServer;
using Shared.Objects;
using Shared.Util;

namespace LobbyServer.Network.Handlers
{
    public static class CreateCharacter
    {
        [Packet(Packets.CmdCreateChar)]
        public static void Handle(Packet packet)
        {
            var createCharPacket = new CreateCharPacket(packet);

            if (packet.Sender.User == null)
            {
                packet.Sender.KillConnection("Session Invalid.");
                return;
            }

            var nameTaken = CharacterModel.CheckNameExists(LobbyServer.Instance.Database.Connection,
                createCharPacket.CharacterName);
            if (nameTaken)
            {
                packet.Sender.SendError("Character name taken!");
                return;
            }

            if (createCharPacket.CarType != 95 || createCharPacket.Color != 16777218)
            {
                Log.Error("Client {0} sent invalid car data!", packet.Sender.EndPoint.Address.ToString());
                packet.Sender.SendError("Invalid car.");
                return;
            }

            var character = new Character()
            {
                Uid = packet.Sender.User.Id,
                Name = createCharPacket.CharacterName,
                Avatar = createCharPacket.Avatar,
                MitoMoney = LobbyServer.Instance.Config.Lobby.NewCharacterMito,
                Hancoin = LobbyServer.Instance.Config.Lobby.NewCharacterHancoin
            };

            CharacterModel.CreateCharacter(LobbyServer.Instance.Database.Connection, ref character);

            var starterVehicle = new Vehicle()
            {
                CarType = createCharPacket.CarType,
                Color = createCharPacket.Color,
                CharacterId = character.Id
            };

            var vehicleId = VehicleModel.Create(
                LobbyServer.Instance.Database.Connection,
                starterVehicle,
                character.Id);

            if (vehicleId <= 0)
            {
                Log.Error("CreateCharacter: failed to create starter vehicle for CID={0}", character.Id);
                packet.Sender.SendError("Unable to create starter vehicle.");
                return;
            }

            character.ActiveVehicleId = (uint)vehicleId;
            starterVehicle.CarId = (uint)vehicleId;
            character.ActiveCar = starterVehicle;

            if (character.GarageVehicles == null)
                character.GarageVehicles = new List<Vehicle>();
            character.GarageVehicles.Add(starterVehicle);

            if (!CharacterModel.Update(LobbyServer.Instance.Database.Connection, character))
            {
                Log.Error("CreateCharacter: failed to update CurrentCarID for CID={0}", character.Id);
                packet.Sender.SendError("Unable to finish character creation.");
                return;
            }

            // CheckInLobby loads this collection once. Keep it synchronized so
            // a UserInfo request immediately after CreateChar sees the new char.
            if (packet.Sender.User.Characters == null)
                packet.Sender.User.Characters = new List<Character>();

            packet.Sender.User.Characters.RemoveAll(c => c.Id == character.Id);
            packet.Sender.User.Characters.Add(character);

            Log.Info("CreateCharacter: created CID={0} Name={1} VehicleID={2} UID={3}",
                character.Id,
                character.Name,
                character.ActiveVehicleId,
                character.Uid);

            packet.Sender.Send(new CreateCharAnswerPacket
            {
                CharacterName = character.Name,
                CharacterId = character.Id,
                ActiveVehicleId = (int)character.ActiveVehicleId,
            }.CreatePacket());
        }
    }
}
