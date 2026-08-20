using GameServer.Util;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers.Join
{
    public class LoadCharThread
    {
        [Packet(Packets.CmdLoadCharThread)]
        public static void Handle(Packet packet)
        {
            var loadCharThreadPacket = new LoadCharThreadPacket(packet);

            var character = CharacterModel.Retrieve(
                GameServer.Instance.Database.Connection,
                loadCharThreadPacket.CharacterName);
            if (character == null)
            {
                packet.Sender.KillConnection("Invalid character selected.");
                return;
            }

            var user = AccountModel.Retrieve(GameServer.Instance.Database.Connection, character.Uid);
            AccountModel.SetActiveCharacter(GameServer.Instance.Database.Connection, user, character.Id);

            packet.Sender.User = user;
            packet.Sender.User.ActiveCharacterId = character.Id;
            packet.Sender.User.ActiveCharacter = character;
            packet.Sender.User.Characters = AccountModel.RetrieveCharacters(
                GameServer.Instance.Database.Connection,
                user.Id);

            // Load the complete runtime state before the client receives its first StatUpdate.
            // Previously the initial packet was a zero-filled research packet that exposed
            // placeholder values such as 1000/9001/9002/9003 in the F2 profile until the
            // inventory was opened and the state happened to be refreshed.
            var vehicles = VehicleModel.Retrieve(GameServer.Instance.Database.Connection, character.Id);
            character.GarageVehicles.Clear();
            character.GarageVehicles.AddRange(vehicles);
            character.ActiveCar = character.GarageVehicles.Find(vehicle =>
                vehicle != null && vehicle.CarId == character.ActiveVehicleId);

            var inventoryItems = ItemModel.RetrieveAll(GameServer.Instance.Database.Connection, character.Id);
            character.InventoryItems.Clear();
            character.InventoryItems.AddRange(inventoryItems);

            if (packet.Sender.User.Permission >= UserPermission.Administrator)
                character.PartyType = 65;

            var ack = new LoadCharThreadAnswer
            {
                ServerId = 0,
                ServerStartTime = 0,
                Character = character,
                Vehicles = vehicles.ToArray(),
                CurrentCarId = (int)character.ActiveVehicleId,
            };
            packet.Sender.Send(ack.CreatePacket());

            SendInitialStats(packet, character);
        }

        private static void SendInitialStats(Packet packet, Character character)
        {
            var activeCar = character.ActiveCar;
            if (activeCar == null)
            {
                Log.Warning(
                    "LoadCharThread: CID={0} has no active vehicle for initial StatUpdate (ActiveVehicleId={1}).",
                    character.Id,
                    character.ActiveVehicleId);
                return;
            }

            var stats = VehicleStatResolver.Resolve(activeCar);
            if (stats == null)
            {
                Log.Warning(
                    "LoadCharThread: unable to resolve initial stats for CID={0} CarId={1} CarType={2} Grade={3}.",
                    character.Id,
                    activeCar.CarId,
                    activeCar.CarType,
                    activeCar.Grade);
                return;
            }

            var equipped = EquippedItemStatResolver.Resolve(character, activeCar);
            var userBonus = (int)character.Level;

            var totalSpeed = stats.Speed + equipped.Speed + userBonus;
            var totalCrash = stats.Crash + equipped.Crash + userBonus;
            var totalAccel = stats.Accel + equipped.Accel + userBonus;
            var totalBoost = stats.Boost + equipped.Boost + userBonus;

            var statAck = new CheckStatAnswer
            {
                BasedSpeed = stats.Speed,
                BasedDurability = stats.Crash,
                BasedAcceleration = stats.Accel,
                BasedBoost = stats.Boost,

                EquipSpeed = equipped.Speed,
                EquipDurability = equipped.Crash,
                EquipAcceleration = equipped.Accel,
                EquipBoost = equipped.Boost,

                CharSpeed = userBonus,
                CharDurability = userBonus,
                CharAcceleration = userBonus,
                CharBoost = userBonus,

                TotalSpeed = totalSpeed,
                TotalDurability = totalCrash,
                TotalAcceleration = totalAccel,
                TotalBoost = totalBoost,

                PerformanceUnknown1 = totalSpeed,
                PerformanceUnknown2 = totalCrash,
                PerformanceUnknown3 = totalAccel,
                PerformanceUnknown4 = totalBoost,
                VehicleSpeed = totalSpeed,
                VehicleDurability = totalCrash,
                VehicleAcceleration = totalAccel,
                VehicleBoost = totalBoost,

                MitronCapacity = stats.MitronCapacity,
                MitronEfficiency = stats.MitronEfficiency
            };

            // Keep the initial packet identical to CmdCheckStat, including any active
            // protocol research configuration, so the client never starts with a stale
            // or structurally different stat layout.
            VehiclePerformanceProbe.Apply(statAck);

            QuietLog.Write(
                "StatUpdate",
                "Initial CID={0} Level={1} CarDbId={2} VehicleId={3} Grade=V{4} Base[S={5},C={6},A={7},B={8}] Equip[S={9},C={10},A={11},B={12}] Total[S={13},C={14},A={15},B={16}]",
                character.Id,
                character.Level,
                activeCar.CarId,
                stats.VehicleId,
                stats.Grade,
                stats.Speed,
                stats.Crash,
                stats.Accel,
                stats.Boost,
                equipped.Speed,
                equipped.Crash,
                equipped.Accel,
                equipped.Boost,
                totalSpeed,
                totalCrash,
                totalAccel,
                totalBoost);

            packet.Sender.Send(statAck.CreatePacket());
        }
    }
}
