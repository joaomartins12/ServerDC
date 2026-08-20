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

            // Preserve the currently active car before switching away from it.
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

            // Vehicle XML grade is the zero-based upgrade row used for a newly purchased car.
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

            // Create the garage vehicle first. VehicleModel.Create assigns the generated DB CID.
            var createdId = VehicleModel.Create(GameServer.Instance.Database.Connection, newVehicle, character.Id);
            if (createdId <= 0)
            {
                Log.Error("BuyCar: failed to persist vehicle {0} ({1}) for CID={2}.",
                    vehicleData.Name, buyCarPacket.CarType, character.Id);
                packet.Sender.SendError("Failed to purchase the car.");
                return;
            }

            // The new car becomes authoritative both in memory and in characters.CurrentCarID.
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

            // Drift City represents owned cars with their matching '<vehicle name> key' inventory item.
            // Items.xml is loaded first and UseItems.xml follows it in ServerMain.Items, so the resulting
            // index is exactly the runtime TableIndex expected by ItemListAck.
            InventoryItem vehicleKey = null;
            var keyTableIndex = FindVehicleKeyTableIndex(vehicleData.Name);
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
                        "BuyCar key granted: CID={0} CarId={1} Vehicle={2} InvenIdx={3} TableIndex={4} ItemId={5} Name={6}",
                        character.Id,
                        newVehicle.CarId,
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
                Log.Warning("BuyCar: no matching key item found for vehicle '{0}'. Expected an item named '{0} key'.",
                    vehicleData.Name);
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

            // Send the real current-car stats instead of the legacy zero-filled StatUpdate.
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

            // A complete ItemList resync makes the newly granted vehicle key immediately visible.
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

        private static int FindVehicleKeyTableIndex(string vehicleName)
        {
            if (ServerMain.Items == null || string.IsNullOrWhiteSpace(vehicleName))
                return -1;

            var expectedName = vehicleName.Trim() + " key";
            for (var i = 0; i < ServerMain.Items.Count; i++)
            {
                var item = ServerMain.Items[i];
                if (item != null && string.Equals((item.Name ?? string.Empty).Trim(), expectedName,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
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
