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
        // Client inventory TableIndex is a global index across ItemClient.tdf followed by
        // UseItemClient.tdf. The current client Data archive contains 1217 ItemClient rows,
        // so a UseItem at zero-based row N is sent as TableIndex = 1217 + N.
        // Example proof from the current client:
        //   pc_0000c Kicker    UseItem row   7 -> TableIndex 1224
        //   pc_0068s Nevera    UseItem row  77 -> TableIndex 1294
        //   pc_0070s Metro     UseItem row  79 -> TableIndex 1296
        //   pc_0264s MITEA ST  UseItem row 199 -> TableIndex 1416
        private const int ClientItemTableRowCount = 1217;

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

            VehicleKeyResearchExporter.LogCandidates(buyCarPacket.CarType, vehicleData.Name);
            VehiclePurchaseResearchExporter.LogPurchase(
                character.Id,
                newVehicle.CarId,
                vehicleRuntimeIndex,
                newVehicle.CarType,
                vehicleData.Name,
                newVehicle.Grade,
                newVehicle.Color);

            InventoryItem grantedKey;
            BasicItem keyData;
            int keyCatalogIndex;
            int keyUseItemIndex;
            int keyProtocolTableIndex;
            string configuredKeyItemId;
            var keyGranted = TryGrantVehicleKey(
                character,
                newVehicle,
                out grantedKey,
                out keyData,
                out keyCatalogIndex,
                out keyUseItemIndex,
                out keyProtocolTableIndex,
                out configuredKeyItemId);

            if (keyGranted)
            {
                Log.Info(
                    "BuyCar key granted from vehicle_catalog: CID={0} CarId={1} CarType={2} Vehicle='{3}' ConfiguredKeyItemId={4} CatalogTableIndex={5} UseItemIndex={6} ProtocolTableIndex={7} ResolvedItemId={8} Name='{9}' InvenIdx={10}",
                    character.Id,
                    newVehicle.CarId,
                    newVehicle.CarType,
                    vehicleData.Name,
                    configuredKeyItemId,
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
                    "BuyCar key not granted: CarId={0} CarType={1} Vehicle='{2}' ConfiguredKeyItemId='{3}'. Check vehicle_catalog.KeyItemId and UseItems.xml.",
                    newVehicle.CarId,
                    newVehicle.CarType,
                    vehicleData.Name,
                    configuredKeyItemId ?? string.Empty);
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
            out int keyProtocolTableIndex,
            out string configuredKeyItemId)
        {
            grantedKey = null;
            keyData = null;
            keyCatalogIndex = -1;
            keyUseItemIndex = -1;
            keyProtocolTableIndex = -1;
            configuredKeyItemId = null;

            if (character == null || vehicle == null || ServerMain.Items == null)
                return false;

            configuredKeyItemId = GetConfiguredKeyItemId(vehicle.CarType);
            if (string.IsNullOrWhiteSpace(configuredKeyItemId))
            {
                Log.Warning("Vehicle key lookup: no KeyItemId configured in vehicle_catalog for CarType={0}.", vehicle.CarType);
                return false;
            }

            var firstUseItemCatalogIndex = FindFirstUseItemCatalogIndex();
            if (firstUseItemCatalogIndex < 0)
                return false;

            for (var i = firstUseItemCatalogIndex; i < ServerMain.Items.Count; i++)
            {
                var useItem = ServerMain.Items[i] as UseItemTable.UseItem;
                if (useItem == null)
                    continue;

                if (!string.Equals(useItem.Id, configuredKeyItemId, StringComparison.OrdinalIgnoreCase))
                    continue;

                keyCatalogIndex = i;
                keyUseItemIndex = i - firstUseItemCatalogIndex;
                keyProtocolTableIndex = checked(ClientItemTableRowCount + keyUseItemIndex);
                keyData = useItem;

                Log.Info(
                    "Vehicle key exact ItemId lookup: CarType={0} ConfiguredKeyItemId={1} ResolvedName='{2}' Category='{3}' CatalogTableIndex={4} UseItemIndex={5} ClientItemRows={6} ProtocolTableIndex={7}",
                    vehicle.CarType,
                    configuredKeyItemId,
                    useItem.Name,
                    useItem.Category,
                    keyCatalogIndex,
                    keyUseItemIndex,
                    ClientItemTableRowCount,
                    keyProtocolTableIndex);
                break;
            }

            if (keyCatalogIndex < 0 || keyData == null)
            {
                Log.Warning(
                    "Vehicle key exact ItemId lookup failed: CarType={0} KeyItemId={1} was not found in loaded UseItems.",
                    vehicle.CarType,
                    configuredKeyItemId);
                return false;
            }

            grantedKey = character.GiveItem(
                GameServer.Instance.Database.Connection,
                keyCatalogIndex,
                1);
            if (grantedKey == null)
                return false;

            grantedKey.TableIndex = keyProtocolTableIndex;
            grantedKey.CarId = vehicle.CarId;
            grantedKey.StackNum = 1;
            grantedKey.State = 0;
            grantedKey.Slot = 0;
            grantedKey.Belonging = 0;

            ItemModel.Update(GameServer.Instance.Database.Connection, grantedKey);

            Log.Debug(
                "BuyCar key persisted: DbId={0} CarId={1} CarType={2} KeyItemId={3} CatalogTableIndex={4} ProtocolTableIndex={5} InvenIdx={6}",
                grantedKey.DbId,
                vehicle.CarId,
                vehicle.CarType,
                configuredKeyItemId,
                keyCatalogIndex,
                keyProtocolTableIndex,
                grantedKey.InventoryIndex);

            return true;
        }

        private static string GetConfiguredKeyItemId(uint carType)
        {
            try
            {
                using (var conn = GameServer.Instance.Database.Connection)
                using (var cmd = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.vehicle_catalog', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.vehicle_catalog', N'KeyItemId') IS NOT NULL
BEGIN
    SELECT KeyItemId
    FROM dbo.vehicle_catalog
    WHERE VehicleId=@vehicleId;
END", conn))
                {
                    cmd.Parameters.AddWithValue("@vehicleId", carType);
                    var value = cmd.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? null
                        : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Vehicle key DB lookup failed for CarType={0}: {1}", carType, ex.Message);
                return null;
            }
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
