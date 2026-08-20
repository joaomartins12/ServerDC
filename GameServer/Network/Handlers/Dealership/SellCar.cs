using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Shared;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
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

            // Trying to sell the selected/current vehicle is a normal invalid operation, not a
            // hack attempt. Keep the connection alive and let the client show an error packet.
            if (vehicleId == character.ActiveVehicleId ||
                (character.ActiveCar != null && vehicleId == character.ActiveCar.CarId))
            {
                Log.Info("SellCar rejected: CID={0} attempted to sell active CarId={1}.", character.Id, vehicleId);
                packet.Sender.SendError("Cannot sell the car currently in use.");
                return;
            }

            var vehicle = character.GarageVehicles == null
                ? null
                : character.GarageVehicles.FirstOrDefault(veh => veh != null && veh.CarId == vehicleId);
            if (vehicle == null)
            {
                Log.Warning("SellCar rejected: CID={0} does not own CarId={1}.", character.Id, vehicleId);
                packet.Sender.SendError("Vehicle not found.");
                return;
            }

            // CarId is the database instance id. Vehicle definitions are indexed by CarType.
            // The old code compared UniqueId against CarId, which made selling a perfectly valid
            // non-active vehicle fail as soon as its DB id differed from its vehicle type.
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
                return;
            }

            // Vehicle instances store the real grade V1..V9. Upgrade catalog rows are 0..8.
            var gradeIndex = vehicle.Grade > 0 ? checked((int)vehicle.Grade - 1) : 0;
            gradeIndex = Math.Max(0, Math.Min(vehicleData.Upgrades.Count - 1, gradeIndex));
            var vehicleUpgrade = vehicleData.Upgrades[gradeIndex];
            if (vehicleUpgrade == null)
            {
                Log.Error("SellCar: missing upgrade row CarType={0} GradeIndex={1}.", vehicle.CarType, gradeIndex);
                packet.Sender.SendError("Failed to sell the car.");
                return;
            }

            int price;
            if (!int.TryParse(vehicleUpgrade.Sell, NumberStyles.Integer, CultureInfo.InvariantCulture, out price))
            {
                Log.Error("SellCar: invalid sell price '{0}' for CarType={1} GradeIndex={2}.",
                    vehicleUpgrade.Sell, vehicle.CarType, gradeIndex);
                packet.Sender.SendError("Failed to sell the car.");
                return;
            }

            // A car can legally be sold while it still has performance parts equipped.
            // Do not call the normal client-driven unequip handler here: the sale flow is already
            // in progress and that handler expects a separate CmdUnEquipItem packet. Instead,
            // detach every equipped inventory item linked to this vehicle directly and persist the
            // authoritative inventory state before removing the vehicle itself.
            var unequippedPartSlots = new List<uint>();
            if (character.InventoryItems != null)
            {
                var equippedParts = character.InventoryItems
                    .Where(item => item != null && item.CarId == vehicleId && item.State != 0)
                    .ToList();

                foreach (var item in equippedParts)
                {
                    var oldSlot = item.Slot;
                    item.LastCarId = vehicleId;
                    item.CarId = 0;
                    item.State = 0;
                    item.Slot = 0;

                    ItemModel.Update(GameServer.Instance.Database.Connection, item);
                    character.AddItemMod(item, true);
                    unequippedPartSlots.Add(item.InventoryIndex);

                    Log.Info(
                        "SellCar auto-unequip: CID={0} CarId={1} InvenIdx={2} TableIndex={3} OldSlot={4} -> inventory",
                        character.Id,
                        vehicleId,
                        item.InventoryIndex,
                        item.TableIndex,
                        oldSlot);
                }
            }

            if (!VehicleModel.Remove(GameServer.Instance.Database.Connection, vehicleId))
            {
                Log.Error("SellCar: couldn't remove CarId={0} from DB after auto-unequipping {1} parts.",
                    vehicleId, unequippedPartSlots.Count);
                packet.Sender.SendError("Failed to sell the car.");
                return;
            }

            character.GarageVehicles.Remove(vehicle);

            // Remove the inventory key that belongs to the sold vehicle. Equipped parts were
            // already detached above (CarId=0), therefore only passive linked items such as the
            // physical vehicle key are removed here.
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

            // Send the authoritative inventory after auto-unequipping the parts and deleting
            // the vehicle key. This avoids the client retaining stale equipped slots.
            packet.Sender.Send(new ItemListAnswer
            {
                InventoryItems = character.InventoryItems == null
                    ? new Shared.Objects.InventoryItem[0]
                    : character.InventoryItems.OrderBy(i => i.InventoryIndex).ToArray()
            }.CreatePacket());

            character.FlushItemModBuffer(packet.Sender);

            Log.Info(
                "SellCar complete: CID={0} CarId={1} CarType={2} Grade=V{3} GradeIndex={4} SellPrice={5} Mito={6} AutoUnequippedParts={7} RemovedLinkedItems={8}",
                character.Id,
                vehicleId,
                vehicle.CarType,
                vehicle.Grade,
                gradeIndex,
                price,
                character.MitoMoney,
                unequippedPartSlots.Count,
                removedKeySlots.Count);
        }
    }
}
