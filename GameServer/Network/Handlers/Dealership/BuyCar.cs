using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameServer.Util;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Objects.GameDatas;
using Shared.Util;

namespace GameServer.Network.Handlers.Dealership
{
    public class BuyCar
    {
        [Packet(Packets.CmdBuyCar)]
        public static void Handle(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null)
            {
                Log.Warning("CmdBuyCar ignored: no active character.");
                return;
            }

            if (character.ActiveCar != null && character.ActiveCar.CarId != 0)
                VehicleModel.Update(GameServer.Instance.Database.Connection, character.ActiveCar);

            var buyCarPacket = new BuyCarPacket(packet);
            var vehicleRuntimeIndex = FindVehicleRuntimeIndex(buyCarPacket.CarType);
            if (vehicleRuntimeIndex < 0)
            {
                Log.Error("BuyCar: vehicle definition not found for CarType={0}.", buyCarPacket.CarType);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            var vehicleData = ServerMain.Vehicles[vehicleRuntimeIndex];
            if (vehicleData == null || vehicleData.Upgrades == null || vehicleData.Upgrades.Count == 0)
            {
                Log.Error("BuyCar: vehicle CarType={0} has no valid upgrade definitions.", buyCarPacket.CarType);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            int gradeIndex;
            if (!int.TryParse(vehicleData.Grade, NumberStyles.Integer, CultureInfo.InvariantCulture, out gradeIndex))
            {
                Log.Error("BuyCar: invalid grade index '{0}' for vehicle {1}.", vehicleData.Grade, vehicleData.Name);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            gradeIndex = Math.Max(0, Math.Min(vehicleData.Upgrades.Count - 1, gradeIndex));
            var realGrade = gradeIndex + 1;
            var vehicleUpgrade = vehicleData.Upgrades[gradeIndex];
            if (vehicleUpgrade == null)
            {
                Log.Error("BuyCar: missing V{0} upgrade definition for vehicle {1}.", realGrade, vehicleData.Name);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            int price;
            if (!int.TryParse(vehicleUpgrade.Price, NumberStyles.Integer, CultureInfo.InvariantCulture, out price))
            {
                Log.Error("BuyCar: invalid V{0} price '{1}' for vehicle {2}.", realGrade, vehicleUpgrade.Price, vehicleData.Name);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            if (character.MitoMoney < price)
            {
                packet.Sender.SendError("Insufficient funds.");
                return;
            }

            var vehicleCount = VehicleModel.RetrieveCount(GameServer.Instance.Database.Connection, character.Id);
            if (vehicleCount >= (character.GarageLevel + 1) * 8)
            {
                packet.Sender.SendError(((char)87u).ToString());
                return;
            }

            var newVehicle = new Vehicle
            {
                CarType = buyCarPacket.CarType,
                BaseColor = 0,
                Grade = checked((uint)realGrade),
                SlotType = 0,
                AuctionCnt = 0,
                Mitron = 100.0f,
                Kmh = 0.0f,
                Color = buyCarPacket.Color,
                AuctionOn = false
            };

            float.TryParse(vehicleUpgrade.Capacity, NumberStyles.Float,
                CultureInfo.InvariantCulture, out newVehicle.MitronCapacity);
            float.TryParse(vehicleUpgrade.Efficiency, NumberStyles.Float,
                CultureInfo.InvariantCulture, out newVehicle.MitronEfficiency);

            var createdId = VehicleModel.Create(GameServer.Instance.Database.Connection, newVehicle, character.Id);
            if (createdId <= 0)
            {
                Log.Error("BuyCar: failed to persist vehicle {0} ({1}) for CID={2}.",
                    vehicleData.Name, buyCarPacket.CarType, character.Id);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            newVehicle.CarId = checked((uint)createdId);
            newVehicle.CharacterId = character.Id;
            character.ActiveCar = newVehicle;
            character.ActiveVehicleId = newVehicle.CarId;

            if (character.GarageVehicles == null)
                character.GarageVehicles = new List<Vehicle>();
            if (!character.GarageVehicles.Any(v => v != null && v.CarId == newVehicle.CarId))
                character.GarageVehicles.Add(newVehicle);

            character.MitoMoney -= price;
            if (!CharacterModel.Update(GameServer.Instance.Database.Connection, character))
            {
                Log.Error("BuyCar: vehicle CID={0} was created but character CurrentCarID/Mito update failed for CID={1}.",
                    newVehicle.CarId, character.Id);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            // Key creation is deliberately disabled while the client TableIndex relation is
            // under research. Earlier guesses produced unrelated Items.xml entries in the UI.
            // We now log the raw UseItems candidates and generate VehicleKeyResearch.csv at
            // server startup; once the mapping is confirmed we can grant the exact key safely.
            VehicleKeyResearchExporter.LogCandidates(buyCarPacket.CarType, vehicleData.Name);
            Log.Warning(
                "BuyCar key grant skipped (research mode): CarId={0} CarType={1} Vehicle='{2}'. See Logs\\Catalogs\\VehicleKeyResearch.csv.",
                newVehicle.CarId, newVehicle.CarType, vehicleData.Name);

            var carInfo = new XiStrCarInfo
            {
                CarID = newVehicle.CarId,
                CarType = newVehicle.CarType,
                BaseColor = newVehicle.BaseColor,
                Grade = newVehicle.Grade,
                SlotType = newVehicle.SlotType,
                AuctionCnt = newVehicle.AuctionCnt,
                Mitron = newVehicle.Mitron,
                Kmh = newVehicle.Kmh,
                Color = newVehicle.Color,
                MitronCapacity = newVehicle.MitronCapacity,
                MitronEfficiency = newVehicle.MitronEfficiency,
                AuctionOn = newVehicle.AuctionOn
            };

            CheckStat.Handle(packet);

            packet.Sender.Send(new VisualUpdateAnswer
            {
                Serial = packet.Sender.User.VehicleSerial,
                Age = 0,
                CarId = newVehicle.CarId,
                CarInfo = carInfo
            }.CreatePacket());

            packet.Sender.Send(new BuyCarAnswer
            {
                CarInfo = carInfo,
                Price = price
            }.CreatePacket());

            packet.Sender.Send(new ItemListAnswer
            {
                InventoryItems = character.InventoryItems.OrderBy(i => i.InventoryIndex).ToArray()
            }.CreatePacket());

            Log.Info(
                "BuyCar complete: CID={0} CarId={1} RuntimeIndex={2} CarType={3} Vehicle={4} GradeIndex={5} Grade=V{6} CurrentCarID={7} GarageCount={8} Price={9} MitoRemaining={10} KeyGranted=false KeyResearch=true",
                character.Id,
                newVehicle.CarId,
                vehicleRuntimeIndex,
                newVehicle.CarType,
                vehicleData.Name,
                gradeIndex,
                realGrade,
                character.ActiveVehicleId,
                character.GarageVehicles.Count,
                price,
                character.MitoMoney);
        }

        private static int FindVehicleRuntimeIndex(uint carType)
        {
            if (ServerMain.Vehicles == null)
                return -1;

            for (var i = 0; i < ServerMain.Vehicles.Count; i++)
            {
                var vehicle = ServerMain.Vehicles[i];
                if (vehicle == null) continue;
                uint id;
                if (uint.TryParse(vehicle.UniqueId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id == carType)
                    return i;
            }

            return -1;
        }
    }
}
