using System;
using System.Text;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.LobbyServer;
using Shared.Objects;
using Shared.Util;

namespace LobbyServer.Network.Handlers
{
    public class CheckInLobby
    {
        [Packet(Packets.CmdCheckInLobby)]
        public static void Handle(Packet packet)
        {
            var checkInLobbyPacket = new CheckInLobbyPacket(packet);
            if (checkInLobbyPacket.ProtocolVersion != ServerMain.ProtocolVersion)
            {
                packet.Sender.SendDebugError("Invalid protocol.");
#if !DEBUG
                packet.Sender.KillConnection("Client outdated!");
#endif
                return;
            }

            var checkInLobbyAnswerPacket = new CheckInLobbyAnswerPacket
            {
                Result = 1,
                Permission = 0x0
            };

            var user = AccountModel.RetrieveFromSession(LobbyServer.Instance.Database.Connection, checkInLobbyPacket.Username,
                checkInLobbyPacket.Ticket);

            Log.Debug("CheckInLobby {0} {1} {2} {3} {4}", checkInLobbyPacket.ProtocolVersion, checkInLobbyPacket.Ticket,
                checkInLobbyPacket.Username, checkInLobbyPacket.Time,
                BitConverter.ToString(Encoding.UTF8.GetBytes(checkInLobbyPacket.StringTicket)));

            // Check if session is really valid, and the client is not tricking us somehow.
            if (user == null)
            {
                Log.Error("Rejecting {0}:{1} (user {2} vs {3}, ticket {4} vs {5}) for invalid user-ticket combination.",
                    packet.Sender.EndPoint.Address.ToString(),
                    packet.Sender.EndPoint.Port,
                    checkInLobbyPacket.Username,
                    packet.Sender.User.Username,
                    checkInLobbyPacket.Ticket,
                    packet.Sender.User.Ticket);
#if DEBUG
                packet.Sender.SendError("Invalid ticket-user combination.");
#else
                packet.Sender.Send(checkInLobbyAnswerPacket.CreatePacket());
                packet.Sender.KillConnection("Invalid ticket-user combination.");
#endif
                return;
            }

            packet.Sender.User = user;

            // Older/partially-created characters could be left with CurrentCarID = -1.
            // SQL Server returns that as Int64 and CharacterModel cannot convert a negative value to UInt32.
            // Normalize the invalid value before hydrating the character object, then repair the active car below.
            using (var repair = new MySqlCommand(
                "UPDATE Characters SET CurrentCarID = 0 WHERE UID = @uid AND CurrentCarID < 0",
                LobbyServer.Instance.Database.Connection))
            {
                repair.Parameters.AddWithValue("@uid", user.Id);
                var repairedRows = repair.ExecuteNonQuery();
                if (repairedRows > 0)
                    Log.Warning("CheckInLobby: normalized {0} invalid CurrentCarID value(s) for UID={1}.", repairedRows, user.Id);
            }

            packet.Sender.User.Characters = AccountModel.RetrieveCharacters(LobbyServer.Instance.Database.Connection, user.Id);

            // Repair characters whose active vehicle reference is missing. This also recovers characters
            // created while the old double-INSERT VehicleModel bug was present.
            foreach (var character in packet.Sender.User.Characters)
            {
                if (character.ActiveCar != null)
                    continue;

                Vehicle activeVehicle = null;
                if (character.GarageVehicles != null && character.GarageVehicles.Count > 0)
                {
                    activeVehicle = character.GarageVehicles[0];
                    Log.Warning(
                        "CheckInLobby: repairing CID={0}; CurrentCarID={1} -> VehicleID={2}.",
                        character.Id,
                        character.ActiveVehicleId,
                        activeVehicle.CarId);
                }
                else
                {
                    activeVehicle = new Vehicle
                    {
                        CarType = 95,
                        Color = 16777218
                    };

                    var newVehicleId = VehicleModel.Create(
                        LobbyServer.Instance.Database.Connection,
                        activeVehicle,
                        character.Id);
                    activeVehicle.CarId = (uint)newVehicleId;
                    activeVehicle.CharacterId = character.Id;
                    character.GarageVehicles.Add(activeVehicle);

                    Log.Warning(
                        "CheckInLobby: CID={0} had no vehicles; created recovery starter VehicleID={1}.",
                        character.Id,
                        activeVehicle.CarId);
                }

                character.ActiveVehicleId = activeVehicle.CarId;
                character.ActiveCar = activeVehicle;
                CharacterModel.Update(LobbyServer.Instance.Database.Connection, character);
            }

            // Send check in lobby answer.
            checkInLobbyAnswerPacket.Result = 0;
            checkInLobbyAnswerPacket.Permission = (int)user.Permission;
            packet.Sender.Send(checkInLobbyAnswerPacket.CreatePacket());

            // Send current lobby time.
            packet.Sender.Send(new LobbyTimeAnswerPacket().CreatePacket());
        }
    }
}
