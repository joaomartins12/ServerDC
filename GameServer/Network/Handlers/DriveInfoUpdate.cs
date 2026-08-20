using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    public class DriveInfoUpdate
    {
        [Packet(Packets.CmdDriveInfoUpdate)]
        public static void Handle(Packet packet)
        {
            var character = packet.Sender.User?.ActiveCharacter;
            var activeCar = character?.ActiveCar;
            if (character == null || activeCar == null)
                return;

            var driveInfo = new DriveInfoPacket(packet);

            // Ignore updates for a different car. This also avoids transferring one
            // vehicle's odometer/fuel state into the currently selected vehicle.
            if (driveInfo.CarId != 0 && driveInfo.CarId != activeCar.CarId)
            {
                Log.Warning(
                    "DriveInfoUpdate: CID={0} sent car {1} while active car is {2}.",
                    character.Id,
                    driveInfo.CarId,
                    activeCar.CarId);
                return;
            }

            var deltaFuel = activeCar.Mitron - driveInfo.TotalFuel;
            if (deltaFuel > 0.0f)
                activeCar.Mitron -= deltaFuel;

            // The original server stores the client's cumulative distance for the active
            // car in CarUnit.Kmh and adds only the positive delta to character mileage.
            var deltaDistance = driveInfo.TotalDistance - activeCar.Kmh;
            if (deltaDistance > 0.0f)
            {
                activeCar.Kmh += deltaDistance;
                character.TotalDistance += deltaDistance;
            }

            if (activeCar.Mitron < 0.0f)
                activeCar.Mitron = 0.0f;

            using (var connection = GameServer.Instance.Database.Connection)
            {
                VehicleModel.Update(connection, activeCar);

                // Previously TotalDistance changed only in memory. Persist it immediately
                // so User Information and relogging use the same mileage value.
                if (deltaDistance > 0.0f)
                    CharacterProgressModel.UpdateMileage(connection, character);
            }
        }
    }
}
