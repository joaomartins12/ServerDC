using System;
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
            var price = 10;
            var vehicleData = ServerMain.Vehicles.Find(vehicle =>
            {
                uint uniqueId;
                return uint.TryParse(vehicle.UniqueId, out uniqueId) && uniqueId == buyCarPacket.CarType;
            });

            if (vehicleData == null)
            {
                Log.Error("BuyCar: vehicle definition not found for CarType={0}.", buyCarPacket.CarType);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            if (vehicleData.Upgrades == null || vehicleData.Upgrades.Count == 0)
            {
                Log.Error("BuyCar: vehicle {0} ({1}) has no upgrade definitions.", vehicleData.Name, buyCarPacket.CarType);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            int vehicleGrade;
            if (!int.TryParse(vehicleData.Grade, out vehicleGrade))
            {
                Log.Error("BuyCar: invalid grade '{0}' for vehicle {1}.", vehicleData.Grade, vehicleData.Name);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            var upgradeIndex = Math.Max(0, Math.Min(vehicleData.Upgrades.Count - 1, vehicleGrade));
            var vehicleUpgrade = vehicleData.Upgrades[upgradeIndex];

            if (!int.TryParse(vehicleUpgrade.Price, out price))
            {
                Log.Error("BuyCar: invalid price '{0}' for vehicle {1}.", vehicleUpgrade.Price, vehicleData.Name);
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
                Grade = (uint)Math.Max(0, vehicleGrade),
                SlotType = 0,
                AuctionCnt = 0,
                Mitron = 100.0f,
                Kmh = 0.0f,
                Color = buyCarPacket.Color,
                AuctionOn = false
            };

            float.TryParse(vehicleUpgrade.Capacity, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out newVehicle.MitronCapacity);
            float.TryParse(vehicleUpgrade.Efficiency, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out newVehicle.MitronEfficiency);

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
                character.GarageVehicles = new System.Collections.Generic.List<Vehicle>();
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

            InventoryItem vehicleKey = null;
            var keyTableIndex = FindVehicleKeyTableIndex(buyCarPacket.CarType, vehicleData.Name);
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
                        "BuyCar key granted: CID={0} CarId={1} CarType={2} Vehicle={3} InvenIdx={4} TableIndex={5} ItemId={6} Name={7}",
                        character.Id,
                        newVehicle.CarId,
                        newVehicle.CarType,
                        vehicleData.Name,
                        vehicleKey.InventoryIndex,
                        keyTableIndex,
                        keyDefinition.Id,
                        keyDefinition.Name);
                }
                else
                {
                    Log.Error("BuyCar: vehicle created but key item could not be persisted for CarId={0} Vehicle={1}.",
                        newVehicle.CarId, vehicleData.Name);
                    vehicleKey = null;
                }
            }
            else
            {
                var expectedId = "pc_" + buyCarPacket.CarType.ToString("x5", System.Globalization.CultureInfo.InvariantCulture);
                Log.Warning(
                    "BuyCar: no matching key item found for CarType={0} Vehicle='{1}'. Expected ItemId={2} (name fallback '{1} key').",
                    buyCarPacket.CarType, vehicleData.Name, expectedId);
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

            if (vehicleKey != null)
            {
                packet.Sender.Send(new ItemListAnswer
                {
                    InventoryItems = character.InventoryItems.OrderBy(i => i.InventoryIndex).ToArray()
                }.CreatePacket());
            }

            Log.Info(
                "BuyCar complete: CID={0} CarId={1} CarType={2} Vehicle={3} CurrentCarID={4} GarageCount={5} Price={6} MitoRemaining={7}",
                character.Id,
                newVehicle.CarId,
                newVehicle.CarType,
                vehicleData.Name,
                character.ActiveVehicleId,
                character.GarageVehicles.Count,
                price,
                character.MitoMoney);
        }

        private static int FindVehicleKeyTableIndex(uint carType, string vehicleName)
        {
            if (ServerMain.Items == null)
                return -1;

            // UseItems vehicle keys encode CarType as five hexadecimal digits.
            // Example: CarType 12 (0x0C) -> pc_0000c; CarType 81 (0x51) -> pc_00051.
            var expectedId = "pc_" + carType.ToString("x5", System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < ServerMain.Items.Count; i++)
            {
                var item = ServerMain.Items[i];
                if (item != null && string.Equals((item.Id ?? string.Empty).Trim(), expectedId,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            // Keep the readable-name lookup as a compatibility fallback for unusual tables.
            if (!string.IsNullOrWhiteSpace(vehicleName))
            {
                var expectedName = vehicleName.Trim() + " key";
                for (var i = 0; i < ServerMain.Items.Count; i++)
                {
                    var item = ServerMain.Items[i];
                    if (item != null && string.Equals((item.Name ?? string.Empty).Trim(), expectedName,
                            StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return -1;
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
