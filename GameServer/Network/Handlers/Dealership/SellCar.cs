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
    public class SellCar
    {
        [Packet(Packets.CmdSellCar)]
        public static void Handle(Packet packet)
        {
            var character = packet.Sender.User == null ? null : packet.Sender.User.ActiveCharacter;
            if (character == null)
            {
                Log.Warning("CmdSellCar ignored: no active character.");
                return;
            }

            var charName = packet.Reader.ReadUnicodeStatic(21);
            var vehicleId = packet.Reader.ReadUInt32();

            if (!string.Equals(charName, character.Name, StringComparison.Ordinal))
            {
                Log.Error("SellCar rejected: character name mismatch. Packet={0} Active={1}", charName, character.Name);
                packet.Sender.SendError("Invalid character.");
                return;
            }

            CompleteSale(packet, character, vehicleId, false);
        }

        public static bool AutoCompleteAfterUnequip(Packet packet, Character character, uint vehicleId)
        {
            if (packet == null || character == null || vehicleId == 0)
                return false;

            if (vehicleId == character.ActiveVehicleId ||
                (character.ActiveCar != null && vehicleId == character.ActiveCar.CarId))
                return false;

            Log.Info("SellCar auto-complete triggered after final unequip: CID={0} CarId={1}.", character.Id, vehicleId);
            return CompleteSale(packet, character, vehicleId, true);
        }

        private static bool CompleteSale(Packet packet, Character character, uint vehicleId, bool autoAfterUnequip)
        {
            if (vehicleId == character.ActiveVehicleId ||
                (character.ActiveCar != null && vehicleId == character.ActiveCar.CarId))
            {
                Log.Info("SellCar rejected: CID={0} attempted to sell active CarId={1}.", character.Id, vehicleId);
                if (!autoAfterUnequip) packet.Sender.SendError("Cannot sell the car currently in use.");
                return false;
            }

            var vehicle = character.GarageVehicles == null
                ? null
                : character.GarageVehicles.FirstOrDefault(veh => veh != null && veh.CarId == vehicleId);
            if (vehicle == null)
            {
                Log.Warning("SellCar skipped: CID={0} does not own CarId={1}.", character.Id, vehicleId);
                if (!autoAfterUnequip) packet.Sender.SendError("Vehicle not found.");
                return false;
            }

            var vehicleData = ServerMain.Vehicles == null
                ? null
                : ServerMain.Vehicles.Find(veh =>
                {
                    uint uniqueId;
                    return veh != null &&
                           uint.TryParse(veh.UniqueId, NumberStyles.Integer, CultureInfo.InvariantCulture, out uniqueId) &&
                           uniqueId == vehicle.CarType;
                });

            if (vehicleData == null || vehicleData.Upgrades == null || vehicleData.Upgrades.Count == 0)
            {
                Log.Error("SellCar: vehicle definition missing for CarId={0} CarType={1}.", vehicleId, vehicle.CarType);
                packet.Sender.SendError("Failed to sell the car.");
                return false;
            }

            var gradeIndex = vehicle.Grade > 0 ? checked((int)vehicle.Grade - 1) : 0;
            gradeIndex = Math.Max(0, Math.Min(vehicleData.Upgrades.Count - 1, gradeIndex));
            var vehicleUpgrade = vehicleData.Upgrades[gradeIndex];
            if (vehicleUpgrade == null)
            {
                packet.Sender.SendError("Failed to sell the car.");
                return false;
            }

            int price;
            if (!int.TryParse(vehicleUpgrade.Sell, NumberStyles.Integer, CultureInfo.InvariantCulture, out price))
            {
                Log.Error("SellCar: invalid sell price '{0}' for CarType={1} GradeIndex={2}.", vehicleUpgrade.Sell, vehicle.CarType, gradeIndex);
                packet.Sender.SendError("Failed to sell the car.");
                return false;
            }

            var unequippedPartSlots = new List<uint>();
            if (character.InventoryItems != null)
            {
                var equippedParts = character.InventoryItems
                    .Where(item => item != null && item.CarId == vehicleId && item.State != 0)
                    .ToList();

                foreach (var item in equippedParts)
                {
                    item.LastCarId = 0;
                    item.CarId = 0;
                    item.State = 0;
                    item.Slot = 0;
                    item.Belonging = 0;
                    ItemModel.Update(GameServer.Instance.Database.Connection, item);
                    character.AddItemMod(item, true);
                    unequippedPartSlots.Add(item.InventoryIndex);
                }
            }

            if (!VehicleModel.Remove(GameServer.Instance.Database.Connection, vehicleId))
            {
                Log.Error("SellCar: couldn't remove CarId={0} from DB.", vehicleId);
                packet.Sender.SendError("Failed to sell the car.");
                return false;
            }

            character.GarageVehicles.Remove(vehicle);

            var removedKeySlots = new List<uint>();
            if (character.InventoryItems != null)
            {
                var linkedItems = character.InventoryItems
                    .Where(item => item != null && item.CarId == vehicleId && item.State == 0)
                    .ToList();

                foreach (var item in linkedItems)
                {
                    if (ItemModel.Remove(GameServer.Instance.Database.Connection, character.Id, checked((int)item.InventoryIndex)))
                    {
                        removedKeySlots.Add(item.InventoryIndex);
                        character.InventoryItems.Remove(item);
                    }
                }
            }

            character.MitoMoney += price;
            if (!CharacterModel.Update(GameServer.Instance.Database.Connection, character))
                Log.Error("SellCar: character update failed after selling CarId={0} CID={1}.", vehicleId, character.Id);

            packet.Sender.Send(new SellCarAnswer
            {
                CarId = checked((int)vehicleId),
                SellPrice = price
            }.CreatePacket());

            packet.Sender.Send(new ItemListAnswer
            {
                InventoryItems = character.InventoryItems == null
                    ? new InventoryItem[0]
                    : character.InventoryItems.OrderBy(i => i.InventoryIndex).ToArray()
            }.CreatePacket());

            character.FlushItemModBuffer(packet.Sender);

            Log.Info(
                "SellCar complete: CID={0} CarId={1} CarType={2} Grade=V{3} GradeIndex={4} SellPrice={5} Mito={6} AutoAfterUnequip={7} AutoUnequippedParts={8} RemovedLinkedItems={9}",
                character.Id, vehicleId, vehicle.CarType, vehicle.Grade, gradeIndex, price,
                character.MitoMoney, autoAfterUnequip, unequippedPartSlots.Count, removedKeySlots.Count);
            return true;
        }
    }
}
