using System;
using System.Data.SqlClient;
using NUnit.Framework;
using Shared.Database;
using Shared.Models;
using Shared.Objects;

namespace SharedTests
{
    [TestFixture]
    public class DatabaseTests
    {
        public static BaseDatabase DbConnection;

        private const string DbHost = "localhost";
        private const int DbPort = 1433;
        private const string DbUsername = "";
        private const string DbPassword = "";
        private const string DbName = "DCServer_Test";

        [OneTimeSetUp]
        public static void Setup()
        {
            // BaseDatabase.Init now handles SQL Server database creation and
            // schema migration automatically. Empty username/password means
            // Windows Authentication.
            DbConnection = new BaseDatabase();
            DbConnection.Init(DbHost, DbPort, DbUsername, DbPassword, DbName);

            using (var conn = DbConnection.Connection)
            {
                Assert.IsNotNull(conn);
                Assert.AreEqual(System.Data.ConnectionState.Open, conn.State);
            }
        }

        [OneTimeTearDown]
        public static void Teardown()
        {
            DbConnection = null;

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = DbHost,
                InitialCatalog = "master",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15
            };

            using (var conn = new SqlConnection(builder.ConnectionString))
            {
                conn.Open();

                // Force-close any test connections so the database can be
                // removed deterministically after the test suite.
                using (var cmd = new SqlCommand(
                    "IF DB_ID(@name) IS NOT NULL " +
                    "BEGIN " +
                    "ALTER DATABASE [" + DbName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    "DROP DATABASE [" + DbName + "]; " +
                    "END", conn))
                {
                    cmd.Parameters.AddWithValue("@name", DbName);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        [Test]
        public static void Test_RetrieveChar()
        {
            using (var conn = DbConnection.Connection)
            {
                var uid = AccountModel.CreateAccount(conn, "127.0.0.1", "admin", "admin");
                var character = new Character
                {
                    Uid = (ulong)uid,
                    Name = "GigaToni",
                    Avatar = 1,
                };

                CharacterModel.CreateCharacter(conn, ref character);
                character.ActiveVehicleId = (uint)VehicleModel.Create(conn, new Vehicle()
                {
                    CarType = 1,
                    Color = 0,
                }, character.Id);
                CharacterModel.Update(conn, character);

                character = CharacterModel.Retrieve(conn, "GigaToni");
                Assert.IsNotNull(character);
                Console.WriteLine(character.Name);
            }
        }
    }
}