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

            InventoryItem vehicleKey = null;
            var coupon = (vehicleUpgrade.Coupon ?? string.Empty).Trim();
            var keyTableIndex = FindVehicleKeyTableIndex(buyCarPacket.CarType, vehicleData.Name, coupon);
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
                        "BuyCar key granted: CID={0} CarId={1} CarType={2} Vehicle={3} Coupon={4} InvenIdx={5} TableIndex={6} ItemId={7} Name={8}",
                        character.Id,
                        newVehicle.CarId,
                        newVehicle.CarType,
                        vehicleData.Name,
                        coupon,
                        vehicleKey.InventoryIndex,
                        keyTableIndex,
                        keyDefinition.Id,
                        keyDefinition.Name);
                }
                else
                {
                    Log.Error("BuyCar: vehicle created but key item could not be persisted for CarId={0} Vehicle={1} Coupon={2}.",
                        newVehicle.CarId, vehicleData.Name, coupon);
                    vehicleKey = null;
                }
            }
            else
            {
                var expectedHexId = "pc_" + buyCarPacket.CarType.ToString("x5", CultureInfo.InvariantCulture);
                var expectedDecimalId = "pc_" + buyCarPacket.CarType.ToString("D5", CultureInfo.InvariantCulture);
                Log.Warning(
                    "BuyCar: no matching key item found. CarType={0} Vehicle='{1}' Coupon='{2}' FallbackHex={3} FallbackDecimal={4} NameFallback='{1} key'.",
                    buyCarPacket.CarType, vehicleData.Name, coupon, expectedHexId, expectedDecimalId);
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

            // Always send the authoritative inventory snapshot after a purchase. This keeps
            // the client in sync even when no key mapping exists and makes a granted key
            // immediately visible without waiting for a separate inventory request.
            packet.Sender.Send(new ItemListAnswer
            {
                InventoryItems = character.InventoryItems.OrderBy(i => i.InventoryIndex).ToArray()
            }.CreatePacket());

            Log.Info(
                "BuyCar complete: CID={0} CarId={1} CarType={2} Vehicle={3} CurrentCarID={4} GarageCount={5} Price={6} MitoRemaining={7} KeyGranted={8} Coupon={9}",
                character.Id,
                newVehicle.CarId,
                newVehicle.CarType,
                vehicleData.Name,
                character.ActiveVehicleId,
                character.GarageVehicles.Count,
                price,
                character.MitoMoney,
                vehicleKey != null,
                coupon);
        }

        private static int FindVehicleKeyTableIndex(uint carType, string vehicleName, string coupon)
        {
            if (ServerMain.Items == null)
                return -1;

            // VehicleList already carries the intended coupon/key relation on each upgrade.
            // Prefer it over guessing from CarType or the display name.
            if (!string.IsNullOrWhiteSpace(coupon) && coupon != "0")
            {
                var direct = FindByItemIdentity(coupon);
                if (direct >= 0)
                    return direct;

                long couponNumber;
                if (TryParseFlexibleNumber(coupon, out couponNumber) && couponNumber >= 0)
                {
                    var numericForms = new[]
                    {
                        couponNumber.ToString(CultureInfo.InvariantCulture),
                        "pc_" + couponNumber.ToString("D5", CultureInfo.InvariantCulture),
                        "pc_" + couponNumber.ToString("x5", CultureInfo.InvariantCulture)
                    };

                    foreach (var candidate in numericForms)
                    {
                        var found = FindByItemIdentity(candidate);
                        if (found >= 0)
                            return found;
                    }

                    // Some data revisions store the item table index in coupon. Only accept
                    // it when the pointed definition actually looks like a vehicle key/coupon.
                    if (couponNumber < ServerMain.Items.Count)
                    {
                        var index = (int)couponNumber;
                        var candidate = ServerMain.Items[index];
                        if (LooksLikeVehicleKey(candidate))
                            return index;
                    }

                    // Finally compare the numeric suffix of pc_* ids in both decimal/hex form.
                    for (var i = 0; i < ServerMain.Items.Count; i++)
                    {
                        var item = ServerMain.Items[i];
                        if (item == null) continue;
                        long suffix;
                        if (TryParsePcSuffix(item.Id, out suffix) && suffix == couponNumber)
                            return i;
                    }
                }
            }

            var expectedHexId = "pc_" + carType.ToString("x5", CultureInfo.InvariantCulture);
            var expectedDecimalId = "pc_" + carType.ToString("D5", CultureInfo.InvariantCulture);

            var byCarType = FindByItemIdentity(expectedHexId);
            if (byCarType >= 0) return byCarType;
            byCarType = FindByItemIdentity(expectedDecimalId);
            if (byCarType >= 0) return byCarType;

            if (!string.IsNullOrWhiteSpace(vehicleName))
            {
                var expectedName = vehicleName.Trim() + " key";
                var expectedCouponName = vehicleName.Trim() + " coupon";
                for (var i = 0; i < ServerMain.Items.Count; i++)
                {
                    var item = ServerMain.Items[i];
                    if (item == null) continue;
                    var name = (item.Name ?? string.Empty).Trim();
                    if (string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, expectedCouponName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return -1;
        }

        private static int FindByItemIdentity(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) || ServerMain.Items == null)
                return -1;

            var expected = identity.Trim();
            for (var i = 0; i < ServerMain.Items.Count; i++)
            {
                var item = ServerMain.Items[i];
                if (item == null) continue;
                if (string.Equals((item.Id ?? string.Empty).Trim(), expected, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static bool LooksLikeVehicleKey(Shared.Objects.GameDatas.BasicItem item)
        {
            if (item == null) return false;
            var id = (item.Id ?? string.Empty).Trim();
            var name = (item.Name ?? string.Empty).Trim();
            return id.StartsWith("pc_", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(" key", StringComparison.OrdinalIgnoreCase) ||
                   name.IndexOf("coupon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryParseFlexibleNumber(string value, out long number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            if (text.StartsWith("pc_", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(3);
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return true;
            return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number);
        }

        private static bool TryParsePcSuffix(string itemId, out long number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(itemId) || !itemId.StartsWith("pc_", StringComparison.OrdinalIgnoreCase))
                return false;
            var suffix = itemId.Substring(3).Trim();
            if (long.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return true;
            return long.TryParse(suffix, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number);
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
