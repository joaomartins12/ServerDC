using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
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

            // Vehicles.xml stores the dealership grade as a zero-based grade index:
            // 0 = V1, 1 = V2, ... 8 = V9. The vehicle instance sent to/persisted for
            // the player uses the real grade number V1..V9, therefore +1 exactly once.
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

            // VehicleCatalog runtime order and the category=car key order are parallel client
            // tables. Example confirmed from the supplied catalogs:
            // runtimeIndex 0 Hyundai Click -> first car key -> Kicker key.
            InventoryItem vehicleKey = null;
            var keyTableIndex = FindVehicleKeyTableIndex(vehicleRuntimeIndex);
            if (keyTableIndex >= 0)
            {
                var inventoryIndex = FindNextInventoryIndex(character);
                vehicleKey = new InventoryItem
                {
                    CharacterId = character.Id,
                    CarId = newVehicle.CarId,
                    LastCarId = 0,
                    State = 0,
                    Slot = 0,
                    StackNum = 1,
                    Belonging = 0,
                    Upgrade = 0,
                    UpgradePoint = 0,
                    Durability = 1.0f,
                    TableIndex = keyTableIndex,
                    InventoryIndex = inventoryIndex,
                    Random = 0
                };

                if (ItemModel.Create(GameServer.Instance.Database.Connection, vehicleKey))
                {
                    character.InventoryItems.Add(vehicleKey);
                    var keyDefinition = ServerMain.Items[keyTableIndex];
                    Log.Info(
                        "BuyCar key granted: CID={0} CarId={1} VehicleRuntimeIndex={2} CarType={3} Vehicle={4} InvenIdx={5} TableIndex={6} ItemId={7} Name={8}",
                        character.Id,
                        newVehicle.CarId,
                        vehicleRuntimeIndex,
                        newVehicle.CarType,
                        vehicleData.Name,
                        vehicleKey.InventoryIndex,
                        keyTableIndex,
                        keyDefinition.Id,
                        keyDefinition.Name);
                }
                else
                {
                    Log.Error("BuyCar: vehicle created but ordered key could not be persisted for CarId={0} Vehicle={1} RuntimeIndex={2}.",
                        newVehicle.CarId, vehicleData.Name, vehicleRuntimeIndex);
                    vehicleKey = null;
                }
            }
            else
            {
                Log.Warning(
                    "BuyCar: no ordered category=car key found for VehicleRuntimeIndex={0} CarType={1} Vehicle='{2}'.",
                    vehicleRuntimeIndex, buyCarPacket.CarType, vehicleData.Name);
            }

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
                "BuyCar complete: CID={0} CarId={1} RuntimeIndex={2} CarType={3} Vehicle={4} GradeIndex={5} Grade=V{6} CurrentCarID={7} GarageCount={8} Price={9} MitoRemaining={10} KeyGranted={11}",
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
                character.MitoMoney,
                vehicleKey != null);
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

        private static int FindVehicleKeyTableIndex(int vehicleRuntimeIndex)
        {
            if (vehicleRuntimeIndex < 0 || ServerMain.Items == null)
                return -1;

            var keyOrdinal = 0;
            for (var tableIndex = 0; tableIndex < ServerMain.Items.Count; tableIndex++)
            {
                var item = ServerMain.Items[tableIndex];
                if (!IsVehicleKey(item))
                    continue;

                if (keyOrdinal == vehicleRuntimeIndex)
                    return tableIndex;

                keyOrdinal++;
            }

            return -1;
        }

        private static bool IsVehicleKey(Shared.Objects.GameDatas.BasicItem item)
        {
            if (item == null) return false;
            if (!string.Equals((item.Category ?? string.Empty).Trim(), "car", StringComparison.OrdinalIgnoreCase))
                return false;

            var name = (item.Name ?? string.Empty).Trim();
            return name.EndsWith("key", StringComparison.OrdinalIgnoreCase);
        }

        private static uint FindNextInventoryIndex(Character character)
        {
            var used = character.InventoryItems == null
                ? new uint[0]
                : character.InventoryItems.Select(i => i.InventoryIndex).OrderBy(i => i).ToArray();

            uint candidate = 0;
            foreach (var index in used)
            {
                if (index < candidate) continue;
                if (index > candidate) break;
                candidate++;
            }
            return candidate;
        }
    }
}
