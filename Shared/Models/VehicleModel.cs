using System;
using System.Collections.Generic;
using System.Data.Common;
using MySql.Data.MySqlClient;
using Shared.Database;
using Shared.Objects;

namespace Shared.Models
{
    public class VehicleModel
    {
        private static void EnsureVisualColorSchema(MySqlConnection dbconn)
        {
            using (var cmd = new MySqlCommand(@"
IF COL_LENGTH('dbo.vehicles','color2') IS NULL
    ALTER TABLE dbo.vehicles ADD color2 BIGINT NOT NULL CONSTRAINT DF_vehicles_color2 DEFAULT (0);", dbconn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        public static void Update(MySqlConnection dbconn, Vehicle vehicle)
        {
            EnsureVisualColorSchema(dbconn);
            using (var cmd = new UpdateCommand("UPDATE vehicles SET {0} WHERE CID=@vehId", dbconn))
            {
                cmd.AddParameter("@vehId", vehicle.CarId);
                var updateCommand = cmd;
                vehicle.WriteToDb(ref updateCommand);
            }
        }

        public static Vehicle Retrieve(MySqlConnection dbconn, uint carId)
        {
            EnsureVisualColorSchema(dbconn);
            var vehicle = new Vehicle();

            var command = new MySqlCommand(
                "SELECT * FROM Vehicles WHERE CID = @car", dbconn);

            command.Parameters.AddWithValue("@car", carId);

            using (DbDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                vehicle.ReadFromDb(reader);
            }

            return vehicle;
        }

        public static List<Vehicle> Retrieve(MySqlConnection dbconn, ulong cid)
        {
            EnsureVisualColorSchema(dbconn);
            var command = new MySqlCommand("SELECT * FROM Vehicles WHERE CharID = @cid", dbconn);

            command.Parameters.AddWithValue("@cid", cid);

            var vehicles = new List<Vehicle>();

            using (DbDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var vehicle = new Vehicle();
                    vehicle.ReadFromDb(reader);
                    vehicles.Add(vehicle);
                }
            }

            return vehicles;
        }

        public static long Create(MySqlConnection dbconn, Vehicle veh, ulong ownerId = 0)
        {
            EnsureVisualColorSchema(dbconn);
            using (var cmd = new InsertCommand("INSERT INTO vehicles {0}", dbconn))
            {
                if (ownerId != 0UL)
                    cmd.Set("CharID", ownerId);

                var insertCommand = cmd;
                veh.WriteToDb(ref insertCommand);

                veh.CarId = (uint)cmd.LastId;
                veh.CharacterId = ownerId;
                return cmd.LastId;
            }
        }

        public static bool Remove(MySqlConnection dbconn, ulong vehId)
        {
            var command = new MySqlCommand("DELETE FROM Vehicles WHERE CID = @vehId", dbconn);
            command.Parameters.AddWithValue("@vehId", vehId);
            return command.ExecuteNonQuery() == 1;
        }

        public static int RetrieveCount(MySqlConnection dbconn, ulong charId)
        {
            var command = new MySqlCommand("SELECT COUNT(*) FROM Vehicles WHERE CharID = @cid", dbconn);

            command.Parameters.AddWithValue("@cid", charId);
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return -1;
                return reader.GetInt32(0);
            }
        }
    }
}
