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
            using (var cmd = new InsertCommand("INSERT INTO `Characters` {0}", dbconn))
            {
                cmd.Set("UID", character.Uid);
                cmd.Set("Name", character.Name);
                cmd.Set("Avatar", character.Avatar);
                cmd.Set("CurrentCarId", -1);
                cmd.Set("City", character.City);
                cmd.Set("CreationDate", DateTimeOffset.Now.ToUnixTimeSeconds());
                cmd.Set("Level", character.Level);
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
    INSERT INTO dbo.character_licenses (CID, LicenseId, UnlockedAt)
    VALUES (@cid, @license, SYSUTCDATETIME());
END;", dbconn))
            {
                insert.Parameters.AddWithValue("@cid", cid);
                insert.Parameters.AddWithValue("@license", DefaultLicenseId);
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
