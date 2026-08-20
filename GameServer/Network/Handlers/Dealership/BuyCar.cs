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
        // Confirmed from a real client purchase of Mittron Fuel (5L):
        // protocol TableIndex = 0x580 + (zero-based UseItems.xml index + 1).
        private const int UseItemProtocolBase = 0x580;

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

            // The vehicle-to-key relation is encoded in UseItems.xml: for category=car keys,
            // the original maxstack field matches the vehicle CarType. The client does not use
            // the merged Items+UseItems index for these entries; UseItems have their own protocol
            // namespace, confirmed by the Mittron Fuel purchase capture.
            VehicleKeyResearchExporter.LogCandidates(buyCarPacket.CarType, vehicleData.Name);

            InventoryItem grantedKey;
            BasicItem keyData;
            int keyCatalogIndex;
            int keyUseItemIndex;
            int keyProtocolTableIndex;
            var keyGranted = TryGrantVehicleKey(
                character,
                newVehicle,
                out grantedKey,
                out keyData,
                out keyCatalogIndex,
                out keyUseItemIndex,
                out keyProtocolTableIndex);

            if (keyGranted)
            {
                Log.Info(
                    "BuyCar key granted: CID={0} CarId={1} CarType={2} Vehicle='{3}' CatalogIndex={4} UseItemIndex={5} ProtocolTableIndex={6} ItemId={7} Name='{8}' InvenIdx={9}",
                    character.Id,
                    newVehicle.CarId,
                    newVehicle.CarType,
                    vehicleData.Name,
                    keyCatalogIndex,
                    keyUseItemIndex,
                    keyProtocolTableIndex,
                    keyData.Id,
                    keyData.Name,
                    grantedKey.InventoryIndex);
            }
            else
            {
                Log.Warning(
                    "BuyCar key not granted: CarId={0} CarType={1} Vehicle='{2}'. No matching category=car key with original maxstack={1} was found or persistence failed.",
                    newVehicle.CarId,
                    newVehicle.CarType,
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

            // GiveItem queues an ItemMod. Flush it only after its TableIndex has been converted
            // to the confirmed UseItem protocol namespace so the client sees the correct key.
            if (keyGranted)
                character.FlushItemModBuffer(packet.Sender);

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
                keyGranted);
        }

        private static bool TryGrantVehicleKey(
            Character character,
            Vehicle vehicle,
            out InventoryItem grantedKey,
            out BasicItem keyData,
            out int keyCatalogIndex,
            out int keyUseItemIndex,
            out int keyProtocolTableIndex)
        {
            grantedKey = null;
            keyData = null;
            keyCatalogIndex = -1;
            keyUseItemIndex = -1;
            keyProtocolTableIndex = -1;

            if (character == null || vehicle == null || ServerMain.Items == null)
                return false;

            var firstUseItemCatalogIndex = FindFirstUseItemCatalogIndex();
            if (firstUseItemCatalogIndex < 0)
                return false;

            for (var i = firstUseItemCatalogIndex; i < ServerMain.Items.Count; i++)
            {
                var useItem = ServerMain.Items[i] as UseItemTable.UseItem;
                if (useItem == null)
                    continue;

                if (!string.Equals(useItem.Category, "car", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(useItem.Name) ||
                    !useItem.Name.EndsWith("key", StringComparison.OrdinalIgnoreCase))
                    continue;

                uint mappedCarType;
                if (!uint.TryParse(useItem.MaxStack, NumberStyles.Integer, CultureInfo.InvariantCulture, out mappedCarType) ||
                    mappedCarType != vehicle.CarType)
                    continue;

                keyCatalogIndex = i;
                keyUseItemIndex = i - firstUseItemCatalogIndex;
                keyProtocolTableIndex = checked(UseItemProtocolBase + keyUseItemIndex + 1);
                keyData = useItem;
                break;
            }

            if (keyCatalogIndex < 0 || keyData == null)
                return false;

            grantedKey = character.GiveItem(
                GameServer.Instance.Database.Connection,
                keyCatalogIndex,
                1);
            if (grantedKey == null)
                return false;

            // GiveItem initially stores the merged server catalog index. Replace it with the
            // TableIndex namespace understood by the client before any ItemMod/ItemList is sent.
            grantedKey.TableIndex = keyProtocolTableIndex;
            grantedKey.CarId = vehicle.CarId;
            grantedKey.StackNum = 1;
            grantedKey.State = 0;
            grantedKey.Slot = 0;
            grantedKey.Belonging = 0;

            if (!ItemModel.Update(GameServer.Instance.Database.Connection, grantedKey))
            {
                Log.Error(
                    "BuyCar key persistence update failed: DbId={0} CarId={1} ProtocolTableIndex={2}.",
                    grantedKey.DbId,
                    vehicle.CarId,
                    keyProtocolTableIndex);
                return false;
            }

            return true;
        }

        private static int FindFirstUseItemCatalogIndex()
        {
            if (ServerMain.Items == null)
                return -1;

            for (var i = 0; i < ServerMain.Items.Count; i++)
            {
                if (ServerMain.Items[i] is UseItemTable.UseItem)
                    return i;
            }

            return -1;
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
