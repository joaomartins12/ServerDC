using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using MySql.Data.MySqlClient;
using Shared.Database;
using Shared.Models;
using Shared.Network;
using Shared.Network.GameServer;
using Shared.Util;

namespace Shared.Objects
{
    public class Character
    {
        /// <summary>
        /// The character Id from DB
        /// </summary>
        public ulong Id;
        public string Name;
        public string LastMessageFrom;
        public int LastDate;
        public ushort Avatar;
        public ushort Level;
        public ExpInfo ExperienceInfo;
        public long MitoMoney;
        public long CrewId;
        public int CrewRank;
        public byte PartyType;
        public uint PvpCount;
        public uint PvpPoint;
        public uint PvpWinCount;
        public uint TeamPvpCount;
        public uint TeamPvpPoint;
        public uint TeamPvpWinCount;
        public uint QuickCount;
        public float TotalDistance;
        public Vector4 Position;
        public int LastChannel;
        public int City;
        public int PosState;
        public uint ActiveVehicleId;
        public uint QuickSlot1;
        public uint QuickSlot2;
        public int TeamJoinDate;
        public int TeamCloseDate;
        public int TeamLeaveDate;
        public int InventoryLevel;
        public int GarageLevel;
        public int Flags = 0x8000000;
        public short Guild;
        public uint GPTeam;
        public int CreationDate;
        public int Hancoin;
        public ulong Uid;
        public Vehicle ActiveCar;
        public Crew Crew;
        public List<InventoryItem> InventoryItems;
        public List<InventoryVisualItem> InventoryVisualItems;
        public List<Vehicle> GarageVehicles;
        private List<ItemMod> ItemModificationBuffer;

        public Character()
        {
            ItemModificationBuffer = new List<ItemMod>();
            InventoryItems = new List<InventoryItem>();
            GarageVehicles = new List<Vehicle>();
            ExperienceInfo = new ExpInfo();
            CreationDate = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            LastDate = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            City = 1;
            Level = 1;
            PosState = 0;
            LastChannel = -1;
        }

        public void AddItemMod(InventoryItem item, bool moved = false)
        {
            ItemModificationBuffer.Add(new ItemMod()
            {
                InventoryItem = item,
                State = moved ? 3 : item.StackNum == 0 ? 2 : 0,
            });
        }

        public void FlushItemModBuffer(Client client)
        {
            var mods = ItemModificationBuffer.ToArray();
            ItemModificationBuffer.Clear();
            client.Send(new ItemModListAnswer()
            {
                Items = mods,
            }.CreatePacket());
        }

        public void SetStartPosition()
        {
            Position.X = -2157.2f + 4 * (new Random().Next() % 10);
            Position.Y = -205.05f + 4.0f * (new Random().Next() % 10);
            Position.Z = 85.720001f + 4.0f * (new Random().Next() % 10);
            Position.W = 90.967003f + 4.0f * (new Random().Next() % 10);
        }

        public void Serialize(BinaryWriterExt writer)
        {
            writer.Write(Id);
            writer.WriteUnicodeStatic(Name, 21);
            writer.WriteUnicodeStatic(LastMessageFrom, 11);
            writer.Write(LastDate);
            writer.Write(Avatar);
            writer.Write(Level);
            writer.Write(ExperienceInfo);
            writer.Write(MitoMoney);
            if (Crew == null)
            {
                writer.Write(-1L);
                writer.Write(0L);
                writer.WriteUnicodeStatic("", 13);
                writer.Write(0);
            }
            else
            {
                writer.Write(Crew.Id);
                writer.Write(Crew.MarkId);
                writer.WriteUnicodeStatic(Crew.Name, 13);
                writer.Write(CrewRank);
            }
            writer.Write(PartyType);
            writer.Write(PvpCount);
            writer.Write(PvpWinCount);
            writer.Write(PvpPoint);
            writer.Write(TeamPvpCount);
            writer.Write(TeamPvpWinCount);
            writer.Write(TeamPvpPoint);
            writer.Write(QuickCount);
            writer.Write(0);
            writer.Write(0);
            writer.Write(TotalDistance);
            writer.Write(Position);
            writer.Write(LastChannel);
            writer.Write(City);
            writer.Write(PosState);
            writer.Write(ActiveVehicleId);
            writer.Write(QuickSlot1);
            writer.Write(QuickSlot2);
            writer.Write(TeamJoinDate);
            writer.Write(TeamCloseDate);
            writer.Write(TeamLeaveDate);
            writer.Write(new byte[12]);
            writer.Write(InventoryLevel);
            writer.Write(GarageLevel);
            writer.Write(new byte[42]);
            writer.Write(Flags);
            writer.Write((int)Guild);
        }

        public void SerializeShort(BinaryWriterExt writer)
        {
            writer.WriteUnicodeStatic(Name, 21);
            writer.Write(Id);
            writer.Write((int)Avatar);
            writer.Write((int)Level);
            writer.Write(ActiveVehicleId);
            writer.Write(ActiveCar.CarType);
            writer.Write(ActiveCar.BaseColor);
            writer.Write(CreationDate);
            writer.Write(CrewId);
            if (Crew != null)
            {
                writer.Write(Crew.MarkId);
                writer.WriteUnicodeStatic(Crew.Name, 13);
                writer.Write((short)CrewRank);
            }
            else
            {
                writer.Write(0L);
                writer.WriteUnicodeStatic("", 13);
                writer.Write((short)0);
            }
            writer.Write(Guild);
        }

        public void CalculateExp(int exp, out bool bLevelChangeOut, bool bUseBonus, bool bUseMita500Bonus)
        {
            bLevelChangeOut = false;
            var newExp = ExperienceInfo.CurExp + exp;
            ushort newLevel = 0;
            long newBaseExp = 0;
            long newNextExp = 0;
            for (var i = 1; i < ServerMain.LevelTable.Count; i++)
            {
                var expLevelInfo = ServerMain.LevelTable[i];
                if (ServerMain.LevelTable[i - 1].Value <= newExp && expLevelInfo.Value > newExp)
                {
                    newBaseExp = ServerMain.LevelTable[i - 1].Value;
                    newNextExp = ServerMain.LevelTable[i].Value;
                    newLevel = expLevelInfo.Key;
                }
            }
#if DEBUG
            Log.Debug($"CaclulateExp: CurExp: {ExperienceInfo.CurExp}, Level: {Level}, BaseExp: {ExperienceInfo.BaseExp}, NextExp {ExperienceInfo.NextExp}");
            Log.Debug($"NEW DATA CaclulateExp: CurExp: {newExp}, Level: {newLevel}, BaseExp: {newBaseExp}, NextExp {newNextExp}");
#endif
            ExperienceInfo.CurExp = newExp;
            if (Level < newLevel)
            {
                Level = newLevel;
                ExperienceInfo.BaseExp = newBaseExp;
                ExperienceInfo.NextExp = newNextExp;
                bLevelChangeOut = true;
            }
        }

        public bool FindFreeSlot(MySqlConnection dbconn, InventoryItem inventoryItem)
        {
            foreach (var item in InventoryItems)
            {
                if (item.TableIndex != inventoryItem.TableIndex) continue;
                item.StackNum++;
                ItemModel.Update(dbconn, item);
                return false;
            }
            return true;
        }

        public void LevelUp()
        {
        }

        /// <summary>
        /// Gives the character the specified item and quantity.
        /// A newly-created inventory item must not inherit the currently active vehicle id.
        /// CarId is an instance link used by equipped parts and vehicle keys; assigning the
        /// active car to an ordinary shop item makes the client interpret that large CarId as
        /// item-instance stat data until the item is equipped/unequipped. Vehicle keys assign
        /// their intended CarId explicitly in BuyCar after GiveItem returns.
        /// </summary>
        public InventoryItem GiveItem(MySqlConnection dbconn, int tableIndex, uint quantity)
        {
            var invIdx = InventoryItems.Count;
            var invItem = new InventoryItem(Id, 0, tableIndex, (uint)invIdx, quantity);
            if (!ItemModel.Create(dbconn, invItem)) return null;

            AddItemMod(invItem);
            InventoryItems.Add(invItem);
            return invItem;
        }

        public bool RemoveItem(MySqlConnection dbconn, int slot, uint quantity)
        {
            if (InventoryItems[slot] == null) return false;
            var itemInSlot = InventoryItems[slot];
            if (itemInSlot.StackNum < quantity) return false;
            if (itemInSlot.StackNum - quantity == 0)
            {
                if (!ItemModel.Remove(dbconn, Id, slot))
                    return false;
                InventoryItems.Remove(itemInSlot);
                itemInSlot.StackNum = 0;
            }
            else
            {
                InventoryItems[slot].StackNum -= quantity;
                itemInSlot.StackNum -= quantity;
                ItemModel.Update(dbconn, InventoryItems[slot]);
            }
            AddItemMod(itemInSlot);
            return true;
        }
    }
}
