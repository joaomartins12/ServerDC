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
        
        /// <summary>
        /// Character Name
        /// Unicode 21 Chars
        /// </summary>
        public string Name;
        
        /// <summary>
        /// ?
        /// 11 (0xB) Chars
        /// </summary>
        public string LastMessageFrom;
        
        /// <summary>
        /// Presumably the last date the character was played
        /// </summary>
        public int LastDate;
        
        /// <summary>
        /// The Avatar the character is using
        /// </summary>
        public ushort Avatar;
        
        /// <summary>
        /// The current level of character
        /// </summary>
        public ushort Level;

        /// <summary>
        /// All about the expierence of the user
        /// </summary>
        public ExpInfo ExperienceInfo;
        
        /// <summary>
        /// How much money the character has
        /// </summary>
        public long MitoMoney;
        
        /// <summary>
        /// The DB Id of the team the user is in
        /// </summary>
        public long CrewId;
        
        /// <summary>
        /// Presumably the rank in the team the char has
        /// TODO: Rename to Crew
        /// 0 = ?
        /// 1 = Crew Master
        /// 2 = ?
        /// 3 = ?
        /// </summary>
        public int CrewRank;
        
        /// <summary>
        /// The party type
        /// 65 != Party is null?
        /// </summary>
        public byte PartyType;
        
        /// <summary>
        /// Presumably How much PvP he has done?
        /// </summary>
        public uint PvpCount;
        
        /// <summary>
        /// Presumably how much Pvp Points
        /// </summary>
        public uint PvpPoint;
        
        /// <summary>
        /// Presumably how many wins he got in PvP
        /// </summary>
        public uint PvpWinCount;
        
        /// <summary>
        /// Presumably Team Pvp Count
        /// <see cref="PvpCount"/>
        /// </summary>
        public uint TeamPvpCount;
        
        /// <summary>
        /// Presumably Team Pvp Points
        /// <see cref="PvpPoint"/>
        /// </summary>
        public uint TeamPvpPoint;
        
        /// <summary>
        /// Presumably Team Pvp Wins
        /// <see cref="PvpWinCount"/>
        /// </summary>
        public uint TeamPvpWinCount;
        
        /// <summary>
        /// Presumably How many Quick services he had?
        /// </summary>
        public uint QuickCount;
        
        /// <summary>
        /// Presumably the total distance traveled
        /// </summary>
        public float TotalDistance;
        
        /// <summary>
        /// The current position & rotation
        /// </summary>
        public Vector4 Position;
        
        /// <summary>
        /// The last channel Id he was in
        /// </summary>
        public int LastChannel;
        
        /// <summary>
        /// The current city Id
        /// 0 = Moon Palace
        /// 1 = Koinonia
        /// 2 = Cras
        /// </summary>
        public int City;

        /// <summary>
        /// The current position state
        /// 0 = Moon Palace Introduction
        /// 2 = Fresh spawn
        /// 3 = ?? (Driver Dome?)
        /// </summary>
        public int PosState;
        
        /// <summary>
        /// Db Id of the car he is driving
        /// </summary>
        public uint ActiveVehicleId;
        
        /// <summary>
        /// TableIndex of Item he has in his first quick slot
        /// </summary>
        public uint QuickSlot1;
        
        /// <summary>
        /// TableIndex of Item he has in his second quick slot
        /// </summary>
        public uint QuickSlot2;
        
        /// <summary>
        /// Unix timestamp when he joined the team
        /// </summary>
        public int TeamJoinDate;
        
        /// <summary>
        /// Unix timestamp when his team got closed
        /// </summary>
        public int TeamCloseDate;
        
        /// <summary>
        /// Unix timestamp when he left his team
        /// </summary>
        public int TeamLeaveDate;
        
        /// <summary>
        /// Zero-based inventory level (pages)
        /// </summary>
        public int InventoryLevel;

        /// <summary>
        /// Zero-based garage level (floors)
        /// </summary>
        public int GarageLevel;
        
        /// <summary>
        /// Some kind of flags
        /// nBattleTutorialCnt |= 0x4000000u
        /// 
        /// enum XiStrCharInfo::FlagType
        /// {
        ///     Beginner_Tutorial = 0x8000000,
        ///     Battle_Tutorial = 0x4000000,
        /// };
        /// </summary>
        public int Flags = 0x8000000;
        
        /// <summary>
        /// Guild/Team (0 = OMD, 1 = ROO)
        /// </summary>
        public short Guild;
        
        /// <summary>
        /// The DCGP Db Id
        /// </summary>
        public uint GPTeam;
        
        /// CharacterInfo End
        
        /// <summary>
        /// Unix timestamp when the chararacter was created
        /// </summary>
        public int CreationDate;

        /// <summary>
        /// Pay2Win currency
        /// </summary>
        public int Hancoin;
        
        /// <summary>
        /// Db Id of the user associated with this character
        /// </summary>
        public ulong Uid;

        //private Vehicle _activeCar;

        /// <summary>
        /// The current active vehicle fetched from Db
        /// NEW: Now points to a car in GarageVehicles
        /// </summary>
        public Vehicle ActiveCar;
        /*{
            get
            { return _activeCar ?? (_activeCar = GarageVehicles.Find(vehicle => vehicle.CarID == ActiveVehicleId)); }
        }*/

        /// <summary>
        /// The team the user is in
        /// </summary>
        public Crew Crew;
        
        /// <summary>
        /// Items in his inventory
        /// Size: 20 * (InventoryLevel+1)
        /// </summary>
        public List<InventoryItem> InventoryItems;

        /// <summary>
        /// Visual Items in his inventory
        /// </summary>
        public List<InventoryVisualItem> InventoryVisualItems;

        public List<Vehicle> GarageVehicles;
        
        /// <summary>
        /// All pending item modifications
        /// Item moved, item added, item deleted / used
        /// </summary>
        private List<ItemMod> ItemModificationBuffer;

        public Character()
        {
            ItemModificationBuffer = new List<ItemMod>();
            InventoryItems = new List<InventoryItem>();
            GarageVehicles = new List<Vehicle>();
            ExperienceInfo = new ExpInfo();
            
            //ActiveCar = new Vehicle();

            // Default vals for new chars
            CreationDate = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            LastDate = (int)DateTimeOffset.Now.ToUnixTimeSeconds();
            
            City = 1; // 0 in SKID_CHARATER db
            Level = 1;
            PosState = 0; // 0 in SKID_CHARATER db
            LastChannel = -1; // This could cause the Issue #26
        }

        /// <summary>
        /// Adds a new inventory modification
        /// </summary>
        /// <param name="item">The item that was modified</param>
        /// <param name="moved">If the item was moved</param>
        public void AddItemMod(InventoryItem item, bool moved = false)
        {
            ItemModificationBuffer.Add(new ItemMod()
            {
                InventoryItem = item,
                State = moved ? 3 : item.StackNum == 0 ? 2 : 0,
            });
        }

        /// <summary>
        /// Flushes the pending inventory modifications
        /// Sends ItemModListAnswer to client
        /// </summary>
        /// <param name="client">The client to send to</param>
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

        /// <summary>
        /// 
        /// </summary>
        /// <param name="writer"></param>
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
            
            writer.Write(PartyType); // possibly 65 when admin or cheatable
            writer.Write(PvpCount);
            writer.Write(PvpWinCount);
            writer.Write(PvpPoint);
            writer.Write(TeamPvpCount);
            writer.Write(TeamPvpWinCount);
            writer.Write(TeamPvpPoint);
            writer.Write(QuickCount);
            writer.Write(0); // unknown
            writer.Write(0); // unknown
            writer.Write(TotalDistance); // NOPE! TotalDistance says 62!
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
            writer.Write(new byte[12]); // filler
            writer.Write(InventoryLevel);
            writer.Write(GarageLevel);
            writer.Write(new byte[42]); // filler
            writer.Write(Flags);
            writer.Write((int)Guild);
            //writer.Write(new byte[38]); // filler
            //writer.Write(GPTeam); // DCGP team
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
                writer.Write(0L); // Is there a reason we send 0L here and not -1L?
                writer.WriteUnicodeStatic("", 13);
                writer.Write((short)0); // Crew rank 
            }
            writer.Write(Guild);
        }

        /// <summary>
        /// Caluclates level from exp get
        /// </summary>
        /// <param name="exp">The exp to get</param>
        /// <param name="bLevelChangeOut">if the user leveledup</param>
        /// <param name="bUseBonus">If we should use a bonus</param>
        /// <param name="bUseMita500Bonus">If we should use a bonus</param>
        /// <returns></returns>
        public void CalculateExp(int exp, out bool bLevelChangeOut, bool bUseBonus, bool bUseMita500Bonus)
        {
            if (bUseBonus)
            {
                /*
                fExp = (float) Exp;
                fBonusExp = *(float*) &FLOAT_0_0;
                if (this->m_bBonusExp == 1)
                    fBonusExp = (float) (fExp * 0.30000001) + 0.0;
                v5 = this->m_EnChantBonus.Exp;
                __asm {
                    lahf
                }
                if (__SETP__(_AH & 0x44, 0))
                    fBonusExp = (float) (fExp * this->m_EnChantBonus.Exp) + fBonusExp;
                if (fBonusExp > 0.0)
                    fExp = fExp + fBonusExp;
                this->m_FExp.m_fFraction = thisa->m_FExp.m_fFraction + fExp;
                v7 = (signed int)ffloor(thisa->m_FExp.m_fFraction);
                thisa->m_FExp.m_fFraction = thisa->m_FExp.m_fFraction - (float) v7;
                Exp = v7;
                */
            }

            if (bUseMita500Bonus)
            {
                /*
                if ( bUseMita500Bonus && this->m_Mita500Buff.m_bBuffState )
                {
                    if ( XiCsCharInfo::GetMita500BuffCheck(this) )
                    {
                        thisa->m_FExp.m_fFraction = thisa->m_FExp.m_fFraction
                                                    + (float)((float)((float)thisa->m_Mita500Buff.m_RewardPoint / 100.0) * (float)Exp);
                        v8 = (signed int)ffloor(thisa->m_FExp.m_fFraction);
                        thisa->m_FExp.m_fFraction = thisa->m_FExp.m_fFraction - (float)v8;
                        Exp = v8;
                    }
                    else
                    {
                        XiCsCharInfo::SetMita500Buff(thisa, 0);
                    }
                }*/
            }

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
                //XiCsCharInfo::ResetChaseFrequency(thisa);
                bLevelChangeOut = true;
                //XiCsCharInfo::LevelUpAddCharacterPoint(thisa, newLevel - oldLevel);
            }
            
            // TODO: Send char update
        }

        public void ChangePosition(MySqlConnection connection, Vector4 newPosition)
        {
            Position = newPosition;
            CharacterModel.Update(connection, this);
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
            // slot is the persistent InventoryIndex sent by the client, not the current
            // zero-based position inside InventoryItems. After earlier deletions the list can
            // contain holes (for example InventoryIndex 16 while List.Count is only 14), so
            // indexing InventoryItems[slot] causes ArgumentOutOfRangeException and disconnects.
            var itemInSlot = InventoryItems.FirstOrDefault(item => item != null && item.InventoryIndex == (uint)slot);
            if (itemInSlot == null) return false;
            if (itemInSlot.StackNum < quantity) return false;

            if (itemInSlot.StackNum == quantity)
            {
                if (!ItemModel.Remove(dbconn, Id, slot))
                    return false;

                InventoryItems.Remove(itemInSlot);
                itemInSlot.StackNum = 0;
            }
            else
            {
                itemInSlot.StackNum -= quantity;
                ItemModel.Update(dbconn, itemInSlot);
            }

            AddItemMod(itemInSlot);
            return true;
        }
    }
}
