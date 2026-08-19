using System;
using System.IO;
using Shared.Database;
using Shared.Models;
using Shared.Util;

namespace AuthServer.Database
{
    public class AuthDatabase : BaseDatabase
    {
        public bool CheckUpdate(string updateFile)
        {
            using (var conn = Connection)
            using (var mc = new MySqlCommand("SELECT * FROM updates WHERE path = @path", conn))
            {
                mc.Parameters.AddWithValue("@path", updateFile);

                using (var reader = mc.ExecuteReader())
                {
                    return reader.Read();
                }
            }
        }

        public void RunUpdate(string updateFile)
        {
            try
            {
                using (var conn = Connection)
                {
                    using (var cmd = new MySqlCommand(File.ReadAllText(Path.Combine("sql", updateFile)), conn))
                    {
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new InsertCommand("INSERT INTO updates {0}", conn))
                    {
                        cmd.Set("path", updateFile);
                        cmd.Execute();
                    }

                    Log.Info("Successfully applied '{0}'.", updateFile);
                }
            }
            catch (Exception ex)
            {
                Log.Error("RunUpdate: Failed to run '{0}': {1}", updateFile, ex.Message);
                ConsoleUtil.Exit(1);
            }
        }
    }
}
