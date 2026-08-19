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
        public static void Update(MySqlConnection dbconn, Vehicle vehicle)
        {
            using (var cmd = new UpdateCommand("UPDATE vehicles SET {0} WHERE CID=@vehId", dbconn))
            {
                cmd.AddParameter("@vehId", vehicle.CarId);
                var updateCommand = cmd;
                vehicle.WriteToDb(ref updateCommand);
                cmd.Execute();
            }
        }

        public static Vehicle Retrieve(MySqlConnection dbconn, uint carId)
        {
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
            using (var cmd = new InsertCommand("INSERT INTO vehicles {0}", dbconn))
            {
                if (ownerId != 0UL)
                    cmd.Set("CharID", ownerId);

                var insertCommand = cmd;
                veh.WriteToDb(ref insertCommand);
                cmd.Execute();
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
