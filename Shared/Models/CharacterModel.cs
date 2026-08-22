using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Numerics;
using MySql.Data.MySqlClient;
using Shared.Database;
using Shared.Objects;
using Shared.Util;

namespace Shared.Models
{
    /// <summary>
    /// </summary>
    public static class CharacterModel
    {
        private const int DefaultLicenseId = 7000;
        private const int LevelOneNextExp = 100;

        public static Character GetCharacter(MySqlConnection dbconn, DbDataReader reader)
        {
            var character = new Character();
            character.Id = Convert.ToUInt64(reader["CID"]);
            character.Uid = Convert.ToUInt64(reader["UID"]);
            character.Name = reader["Name"] as string;
            character.CreationDate = Convert.ToInt32(reader["CreationDate"]);
            character.MitoMoney = Convert.ToInt64(reader["Mito"]);
            character.Hancoin = Convert.ToInt32(reader["Hancoin"]);
            character.Avatar = Convert.ToUInt16(reader["Avatar"]);
            character.Guild = Convert.ToInt16(reader["Guild"]);
            character.Level = Convert.ToUInt16(reader["Level"]);
            
            character.ExperienceInfo.BaseExp = Convert.ToInt32(reader["BaseExp"]);
            character.ExperienceInfo.CurExp = Convert.ToInt32(reader["CurExp"]);
            character.ExperienceInfo.NextExp = Convert.ToInt32(reader["NextExp"]);
            NormalizeExperience(character);

            character.TotalDistance = Convert.ToInt32(reader["Mileage"]);
            character.City = Convert.ToInt32(reader["City"]);
            character.ActiveVehicleId = Convert.ToUInt32(reader["CurrentCarID"]);
            character.InventoryLevel = Convert.ToInt32(reader["InventoryLevel"]);
            character.GarageLevel = Convert.ToInt32(reader["GarageLevel"]);
            character.CrewId = Convert.ToInt64(reader["TeamId"]);
            character.CrewRank = Convert.ToInt32(reader["TeamRank"]);
            character.Position = new Vector4(Convert.ToSingle(reader["posX"]), Convert.ToSingle(reader["posY"]), Convert.ToSingle(reader["posZ"]), Convert.ToSingle(reader["posW"]));
            character.LastChannel = Convert.ToInt32(reader["channelId"]);
            character.PosState = Convert.ToInt32(reader["posState"]);
            return character;
        }

        public static int WriteCharacter(Character character, UpdateCommand cmd)
        {
            NormalizeExperience(character);

            cmd.Set("Name", character.Name);
            cmd.Set("CreationDate", character.CreationDate);
            cmd.Set("Mito", character.MitoMoney);
            cmd.Set("Hancoin", character.Hancoin);
            cmd.Set("Avatar", character.Avatar);
            cmd.Set("Level", character.Level);
            cmd.Set("BaseExp", character.ExperienceInfo.BaseExp);
            cmd.Set("CurExp", character.ExperienceInfo.CurExp);
            cmd.Set("NextExp", character.ExperienceInfo.NextExp);
            cmd.Set("Mileage", character.TotalDistance);
            cmd.Set("City", character.City);
            cmd.Set("CurrentCarID", character.ActiveVehicleId);
            cmd.Set("InventoryLevel", character.InventoryLevel);
            cmd.Set("GarageLevel", character.GarageLevel);
            cmd.Set("TeamId", character.CrewId);
            cmd.Set("TeamRank", character.CrewRank);
            cmd.Set("posX", character.Position.X);
            cmd.Set("posY", character.Position.Y);
            cmd.Set("posZ", character.Position.Z);
            cmd.Set("posW", character.Position.W);
            cmd.Set("channelId", character.LastChannel);
            cmd.Set("posState", character.PosState);

            return cmd.Execute();
        }
        
        public static Character Retrieve(MySqlConnection dbconn, string characterName)
        {
            var command = new MySqlCommand(
                "SELECT * FROM Characters WHERE characters.Name = @char", dbconn);

            command.Parameters.AddWithValue("@char", characterName);

            Character character;
            using (DbDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                character = GetCharacter(dbconn, reader);
            }

            // GetCharacter normalizes the live object while the reader is open. Persisting
            // happens only after the reader has closed, so legacy 0/0 rows heal on login.
            EnsureCharacterExperience(dbconn, character);

            character.GarageVehicles = VehicleModel.Retrieve(dbconn, character.Id);
            character.ActiveCar = character.GarageVehicles.Find(vehicle => vehicle.CarId == character.ActiveVehicleId);
            character.Crew = CrewModel.Retrieve(dbconn, character.CrewId);
            return character;
        }

        public static Character Retrieve(MySqlConnection dbconn, ulong cid)
        {
            var command = new MySqlCommand(
                "SELECT * FROM Characters WHERE characters.CID = @cid", dbconn);

            command.Parameters.AddWithValue("@cid", cid);

            Character character;

            using (DbDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                character = GetCharacter(dbconn, reader);
            }

            EnsureCharacterExperience(dbconn, character);

            character.GarageVehicles = VehicleModel.Retrieve(dbconn, character.Id);
            character.ActiveCar = character.GarageVehicles.Find(vehicle => vehicle.CarId == character.ActiveVehicleId);
            character.Crew = CrewModel.Retrieve(dbconn, character.CrewId);
            return character;
        }
        
        public static bool HasCharacter(MySqlConnection dbconn, ulong cid, ulong uid)
        {
            var command = new MySqlCommand(
                "SELECT * FROM Characters WHERE CID = @cid AND UID = @uid", dbconn);

            command.Parameters.AddWithValue("@cid", cid);
            command.Parameters.AddWithValue("@uid", uid);

            using (DbDataReader reader = command.ExecuteReader())
            {
                return reader.HasRows;
            }
        }

        public static ulong HasCharacter(MySqlConnection dbconn, string characterName, ulong uid)
        {
            var command = new MySqlCommand(
                "SELECT `CID` FROM Characters WHERE Name = @charName AND UID = @uid", dbconn);

            command.Parameters.AddWithValue("@charName", characterName);
            command.Parameters.AddWithValue("@uid", uid);

            using (DbDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read()) return 0;
                return reader.HasRows ? Convert.ToUInt64(reader["CID"]) : 0;
            }
        }
        
        public static void DeleteCharacter(MySqlConnection dbconn, ulong cid, ulong uid)
        {
            var command = new MySqlCommand("DELETE FROM Characters WHERE CID = @cid AND UID = @uid", dbconn);
            command.Parameters.AddWithValue("@cid", cid);
            command.Parameters.AddWithValue("@uid", uid);
            command.ExecuteNonQuery();
        }

        public static bool CheckNameExists(MySqlConnection dbconn, string characterName)
        {
            var command = new MySqlCommand(
                "SELECT * FROM Characters WHERE Name = @charName", dbconn);

            command.Parameters.AddWithValue("@charName", characterName);

            using (DbDataReader reader = command.ExecuteReader())
            {
                return reader.HasRows;
            }
        }

        public static void CreateCharacter(MySqlConnection dbconn, ref Character character)
        {
            // New characters must start with a valid LevelServer interval. Previously the
            // INSERT relied on DB defaults, then CharacterModel.Update wrote the in-memory
            // 0/0 ExpInfo over NextExp=100 after starter-car creation.
            NormalizeExperience(character);

            using (var cmd = new InsertCommand("INSERT INTO `Characters` {0}", dbconn))
            {
                cmd.Set("UID", character.Uid);
                cmd.Set("Name", character.Name);
                cmd.Set("Avatar", character.Avatar);
                cmd.Set("CurrentCarId", -1);
                cmd.Set("City", character.City);
                cmd.Set("CreationDate", DateTimeOffset.Now.ToUnixTimeSeconds());
                cmd.Set("Level", character.Level);
                cmd.Set("BaseExp", character.ExperienceInfo.BaseExp);
                cmd.Set("CurExp", character.ExperienceInfo.CurExp);
                cmd.Set("NextExp", character.ExperienceInfo.NextExp);
                cmd.Set("GarageLevel", character.GarageLevel);
                cmd.Set("InventoryLevel", character.InventoryLevel);
                cmd.Set("posState", character.PosState);
                cmd.Set("channelId", character.LastChannel);
                cmd.Set("Mito", character.MitoMoney);
                cmd.Set("Hancoin", character.Hancoin);
                cmd.Set("CurrentLicenseId", DefaultLicenseId);

                cmd.Execute();
                character.Id = (ulong)cmd.LastId;
            }

            EnsureDefaultLicense(dbconn, character.Id);
        }

        /// <summary>
        /// Startup repair for the legacy character-creation bug. It intentionally targets
        /// only the unambiguous level-one invalid interval; higher levels require the
        /// LevelServer table and must not be guessed here.
        /// </summary>
        public static int RepairInvalidExperienceRows(MySqlConnection dbconn)
        {
            if (dbconn == null) return 0;
            using (var cmd = new MySqlCommand(@"
UPDATE dbo.characters
SET Level=1,
    BaseExp=0,
    CurExp=CASE WHEN ISNULL(CurExp,0)>=0 AND ISNULL(CurExp,0)<@next THEN ISNULL(CurExp,0) ELSE 0 END,
    NextExp=@next
WHERE ISNULL(Level,0)<=1
  AND (ISNULL(NextExp,0)<=ISNULL(BaseExp,0)
       OR ISNULL(NextExp,0)<=ISNULL(CurExp,0)
       OR ISNULL(NextExp,0)<=0
       OR ISNULL(BaseExp,0)<0
       OR ISNULL(CurExp,0)<0);", dbconn))
            {
                cmd.Parameters.AddWithValue("@next", LevelOneNextExp);
                var repaired = cmd.ExecuteNonQuery();
                if (repaired > 0)
                    Log.Warning("Character EXP repair: restored {0} invalid level-one row(s) to Base=0 Next=100.", repaired);
                return repaired;
            }
        }

        private static bool NormalizeExperience(Character character)
        {
            if (character == null) return false;
            var changed = false;

            if (character.Level < 1)
            {
                character.Level = 1;
                changed = true;
            }

            if (character.Level == 1 &&
                (character.ExperienceInfo.NextExp <= character.ExperienceInfo.BaseExp ||
                 character.ExperienceInfo.NextExp <= character.ExperienceInfo.CurExp ||
                 character.ExperienceInfo.NextExp <= 0 ||
                 character.ExperienceInfo.BaseExp < 0 ||
                 character.ExperienceInfo.CurExp < 0))
            {
                character.ExperienceInfo.BaseExp = 0;
                if (character.ExperienceInfo.CurExp < 0 || character.ExperienceInfo.CurExp >= LevelOneNextExp)
                    character.ExperienceInfo.CurExp = 0;
                character.ExperienceInfo.NextExp = LevelOneNextExp;
                changed = true;
            }

            return changed;
        }

        private static void EnsureCharacterExperience(MySqlConnection dbconn, Character character)
        {
            if (dbconn == null || character == null || character.Id == 0) return;

            // GetCharacter may already have normalized the object. Persist level-one rows
            // regardless so an in-memory repair is guaranteed to reach the database.
            var changed = NormalizeExperience(character);
            if (!changed && character.Level != 1) return;

            using (var cmd = new MySqlCommand(@"
UPDATE dbo.characters
SET Level=@level, BaseExp=@base, CurExp=@cur, NextExp=@next
WHERE CID=@cid;", dbconn))
            {
                cmd.Parameters.AddWithValue("@level", character.Level);
                cmd.Parameters.AddWithValue("@base", character.ExperienceInfo.BaseExp);
                cmd.Parameters.AddWithValue("@cur", character.ExperienceInfo.CurExp);
                cmd.Parameters.AddWithValue("@next", character.ExperienceInfo.NextExp);
                cmd.Parameters.AddWithValue("@cid", character.Id);
                cmd.ExecuteNonQuery();
            }

            if (changed)
            {
                Log.Warning("Character EXP repaired on load: CID={0} Name={1} Level={2} Base={3} Cur={4} Next={5}",
                    character.Id, character.Name ?? string.Empty, character.Level,
                    character.ExperienceInfo.BaseExp, character.ExperienceInfo.CurExp, character.ExperienceInfo.NextExp);
            }
        }

        public static void EnsureDefaultLicense(MySqlConnection dbconn, ulong cid)
        {
            if (dbconn == null || cid == 0) return;

            using (var update = new MySqlCommand(@"
UPDATE dbo.characters
SET CurrentLicenseId = @license
WHERE CID = @cid
  AND (CurrentLicenseId IS NULL OR CurrentLicenseId <= 0);", dbconn))
            {
                update.Parameters.AddWithValue("@license", DefaultLicenseId);
                update.Parameters.AddWithValue("@cid", cid);
                update.ExecuteNonQuery();
            }

            using (var insert = new MySqlCommand(@"
IF NOT EXISTS (
    SELECT 1 FROM dbo.character_licenses WHERE CID=@cid AND LicenseId=@license
)
BEGIN
    INSERT INTO dbo.character_licenses (CID, LicenseId, UnlockedDate, IsNew)
    VALUES (@cid, @license, @time, 1);
END;", dbconn))
            {
                insert.Parameters.AddWithValue("@cid", cid);
                insert.Parameters.AddWithValue("@license", DefaultLicenseId);
                insert.Parameters.AddWithValue("@time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                insert.ExecuteNonQuery();
            }
        }

        public static bool Update(MySqlConnection dbconn, Character character)
        {
            using (var cmd = new UpdateCommand("UPDATE `Characters` SET {0} WHERE `CID` = @charId", dbconn))
            {
                cmd.AddParameter("@charId", character.Id);
                return WriteCharacter(character, cmd) == 1;
            }
        }
    }
}
