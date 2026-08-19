using System;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Threading;
using Shared.Models;

namespace Shared.Models
{
    /// <summary>
    /// Compatibility connection used by the legacy database models.
    /// Despite the historical name, this is backed by Microsoft SQL Server.
    /// </summary>
    public sealed class MySqlConnection : IDisposable
    {
        internal SqlConnection InnerConnection { get; }

        public MySqlConnection(string connectionString)
        {
            InnerConnection = new SqlConnection(connectionString);
        }

        public ConnectionState State => InnerConnection.State;

        public void Open() => InnerConnection.Open();
        public void Close() => InnerConnection.Close();

        public MySqlTransaction BeginTransaction()
        {
            return new MySqlTransaction(InnerConnection.BeginTransaction());
        }

        public void Dispose()
        {
            InnerConnection.Dispose();
        }
    }

    public sealed class MySqlTransaction : IDisposable
    {
        internal SqlTransaction InnerTransaction { get; }

        internal MySqlTransaction(SqlTransaction transaction)
        {
            InnerTransaction = transaction;
        }

        public void Commit() => InnerTransaction.Commit();
        public void Rollback() => InnerTransaction.Rollback();
        public void Dispose() => InnerTransaction.Dispose();
    }

    public sealed class MySqlParameterCollection
    {
        private readonly SqlParameterCollection _parameters;

        internal MySqlParameterCollection(SqlParameterCollection parameters)
        {
            _parameters = parameters;
        }

        public SqlParameter AddWithValue(string parameterName, object value)
        {
            return _parameters.AddWithValue(parameterName, NormalizeValue(value));
        }

        private static object NormalizeValue(object value)
        {
            if (value == null)
                return DBNull.Value;

            var type = value.GetType();
            if (type.IsEnum)
                value = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);

            if (value is ulong ul) return checked((long)ul);
            if (value is uint ui) return (long)ui;
            if (value is ushort us) return (int)us;
            if (value is sbyte sb) return (short)sb;
            if (value is char c) return c.ToString();

            return value;
        }
    }

    /// <summary>
    /// Compatibility command used by legacy code. SQL text is normalized from
    /// the small amount of MySQL quoting still present in old queries.
    /// </summary>
    public sealed class MySqlCommand : IDisposable
    {
        private readonly SqlCommand _command;
        private long _lastInsertedId;

        public MySqlCommand(string commandText, MySqlConnection connection)
            : this(commandText, connection, null)
        {
        }

        public MySqlCommand(string commandText, MySqlConnection connection, MySqlTransaction transaction)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            _command = new SqlCommand(NormalizeSql(commandText), connection.InnerConnection);
            if (transaction != null)
                _command.Transaction = transaction.InnerTransaction;

            Parameters = new MySqlParameterCollection(_command.Parameters);
        }

        public MySqlParameterCollection Parameters { get; }

        public string CommandText
        {
            get => _command.CommandText;
            set => _command.CommandText = NormalizeSql(value);
        }

        public long LastInsertedId => _lastInsertedId;

        public SqlDataReader ExecuteReader()
        {
            _command.CommandText = NormalizeSql(_command.CommandText);
            return _command.ExecuteReader();
        }

        public object ExecuteScalar()
        {
            _command.CommandText = NormalizeSql(_command.CommandText);
            return _command.ExecuteScalar();
        }

        public int ExecuteNonQuery()
        {
            _command.CommandText = NormalizeSql(_command.CommandText);

            var trimmed = _command.CommandText.TrimStart();
            if (trimmed.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase))
            {
                var original = _command.CommandText.TrimEnd().TrimEnd(';');
                _command.CommandText = original + "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";
                var result = _command.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    _lastInsertedId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                return 1;
            }

            return _command.ExecuteNonQuery();
        }

        private static string NormalizeSql(string sql)
        {
            return (sql ?? string.Empty).Replace("`", string.Empty);
        }

        public void Dispose()
        {
            _command.Dispose();
        }
    }
}

namespace Shared.Database
{
    public class BaseDatabase
    {
        private const string DefaultDatabaseName = "DCServer";
        private string _connectionString;

        /// <summary>
        /// Returns an opened SQL Server connection.
        /// </summary>
        public MySqlConnection Connection
        {
            get
            {
                if (_connectionString == null)
                    throw new Exception("Database has not been initialized.");

                var result = new MySqlConnection(_connectionString);
                result.Open();
                return result;
            }
        }

        /// <summary>
        /// Initializes Microsoft SQL Server, creates the database/schema when
        /// necessary and verifies the final connection.
        /// </summary>
        public void Init(string host, int port, string user, string pass, string db)
        {
            host = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();
            db = string.IsNullOrWhiteSpace(db) ? DefaultDatabaseName : db.Trim();

            ValidateIdentifier(db);

            var server = BuildServerName(host, port);
            var masterConnectionString = BuildConnectionString(server, "master", user, pass);
            _connectionString = BuildConnectionString(server, db, user, pass);

            EnsureDatabase(masterConnectionString, db);
            EnsureSchema(_connectionString);
            TestConnection();
        }

        public void TestConnection()
        {
            MySqlConnection conn = null;
            try
            {
                conn = Connection;
            }
            finally
            {
                conn?.Close();
                conn?.Dispose();
            }
        }

        private static string BuildServerName(string host, int port)
        {
            // Named instances (localhost\\SQLEXPRESS) should not receive a port.
            if (host.Contains("\\") || port <= 0 || port == 1433)
                return host;

            return host + "," + port;
        }

        private static string BuildConnectionString(string server, string database, string user, string pass)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                IntegratedSecurity = string.IsNullOrWhiteSpace(user),
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity Server"
            };

            if (!builder.IntegratedSecurity)
            {
                builder.UserID = user;
                builder.Password = pass ?? string.Empty;
            }

            return builder.ConnectionString;
        }

        private static void EnsureDatabase(string masterConnectionString, string databaseName)
        {
            // Several server executables can start at once. Serialize the initial
            // database creation locally so they do not race each other.
            using (var mutex = new Mutex(false, @"Local\DCServer.DatabaseInitialization"))
            {
                var lockTaken = false;
                try
                {
                    lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(60));
                    if (!lockTaken)
                        throw new TimeoutException("Timed out waiting for database initialization lock.");

                    using (var conn = new SqlConnection(masterConnectionString))
                    {
                        conn.Open();
                        using (var cmd = new SqlCommand("SELECT DB_ID(@name)", conn))
                        {
                            cmd.Parameters.AddWithValue("@name", databaseName);
                            var databaseId = cmd.ExecuteScalar();
                            if (databaseId == null || databaseId == DBNull.Value)
                            {
                                using (var create = new SqlCommand("CREATE DATABASE [" + databaseName + "]", conn))
                                    create.ExecuteNonQuery();
                            }
                        }
                    }
                }
                finally
                {
                    if (lockTaken)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static void EnsureSchema(string connectionString)
        {
            using (var conn = new SqlConnection(connectionString))
            {
                conn.Open();
                using (var cmd = new SqlCommand(SchemaSql, conn))
                {
                    cmd.CommandTimeout = 60;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void ValidateIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Database name cannot be empty.");

            foreach (var c in value)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                    throw new ArgumentException("Database name contains invalid characters: " + value);
            }
        }

        private const string SchemaSql = @"
IF OBJECT_ID(N'dbo.users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.users
    (
        UID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY,
        Username VARCHAR(21) NOT NULL,
        Password VARCHAR(64) NOT NULL,
        Salt VARCHAR(64) NOT NULL,
        Ticket BIGINT NOT NULL,
        Status TINYINT NOT NULL CONSTRAINT DF_users_Status DEFAULT (1),
        CreateIP VARCHAR(45) NOT NULL CONSTRAINT DF_users_CreateIP DEFAULT ('127.0.0.1'),
        CreateDate BIGINT NOT NULL CONSTRAINT DF_users_CreateDate DEFAULT (0),
        Permission INT NOT NULL CONSTRAINT DF_users_Permission DEFAULT (0),
        LastActiveChar BIGINT NULL CONSTRAINT DF_users_LastActiveChar DEFAULT (0),
        BanValidUntil BIGINT NULL CONSTRAINT DF_users_BanValidUntil DEFAULT (0),
        VehicleSerial INT NULL CONSTRAINT DF_users_VehicleSerial DEFAULT (0)
    );
    CREATE UNIQUE INDEX UX_users_Username ON dbo.users(Username);
END;

IF OBJECT_ID(N'dbo.characters', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.characters
    (
        CID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_characters PRIMARY KEY,
        UID BIGINT NOT NULL,
        Name VARCHAR(21) NOT NULL,
        CreationDate BIGINT NOT NULL,
        Mito BIGINT NULL CONSTRAINT DF_characters_Mito DEFAULT (1000),
        Avatar INT NULL CONSTRAINT DF_characters_Avatar DEFAULT (0),
        Level INT NULL CONSTRAINT DF_characters_Level DEFAULT (1),
        BaseExp BIGINT NULL CONSTRAINT DF_characters_BaseExp DEFAULT (0),
        CurExp BIGINT NULL CONSTRAINT DF_characters_CurExp DEFAULT (0),
        NextExp BIGINT NULL CONSTRAINT DF_characters_NextExp DEFAULT (100),
        City INT NULL CONSTRAINT DF_characters_City DEFAULT (1),
        CurrentCarID BIGINT NULL CONSTRAINT DF_characters_CurrentCarID DEFAULT (1),
        GarageLevel INT NULL CONSTRAINT DF_characters_GarageLevel DEFAULT (0),
        InventoryLevel INT NULL CONSTRAINT DF_characters_InventoryLevel DEFAULT (0),
        posX FLOAT NULL CONSTRAINT DF_characters_posX DEFAULT (0),
        posY FLOAT NULL CONSTRAINT DF_characters_posY DEFAULT (0),
        posZ FLOAT NULL CONSTRAINT DF_characters_posZ DEFAULT (0),
        posW FLOAT NULL CONSTRAINT DF_characters_posW DEFAULT (0),
        channelId INT NULL,
        posState INT NULL CONSTRAINT DF_characters_posState DEFAULT (0),
        Mileage BIGINT NULL CONSTRAINT DF_characters_Mileage DEFAULT (0),
        TeamId BIGINT NULL CONSTRAINT DF_characters_TeamId DEFAULT (-1),
        TeamRank INT NULL CONSTRAINT DF_characters_TeamRank DEFAULT (-1),
        Guild INT NULL CONSTRAINT DF_characters_Guild DEFAULT (0),
        Hancoin INT NULL CONSTRAINT DF_characters_Hancoin DEFAULT (0),
        CONSTRAINT FK_characters_users FOREIGN KEY (UID) REFERENCES dbo.users(UID)
    );
    CREATE INDEX IX_characters_UID ON dbo.characters(UID);
    CREATE UNIQUE INDEX UX_characters_Name ON dbo.characters(Name);
END;

IF OBJECT_ID(N'dbo.vehicles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.vehicles
    (
        CID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_vehicles PRIMARY KEY,
        CharID BIGINT NOT NULL,
        auctionCount INT NOT NULL CONSTRAINT DF_vehicles_auctionCount DEFAULT (0),
        baseColor BIGINT NOT NULL CONSTRAINT DF_vehicles_baseColor DEFAULT (0),
        carType BIGINT NOT NULL CONSTRAINT DF_vehicles_carType DEFAULT (24),
        grade INT NOT NULL CONSTRAINT DF_vehicles_grade DEFAULT (9),
        mitron FLOAT NOT NULL CONSTRAINT DF_vehicles_mitron DEFAULT (0),
        kmh FLOAT NOT NULL CONSTRAINT DF_vehicles_kmh DEFAULT (0),
        slotType INT NOT NULL CONSTRAINT DF_vehicles_slotType DEFAULT (0),
        color BIGINT NOT NULL CONSTRAINT DF_vehicles_color DEFAULT (0),
        mitronCapacity FLOAT NOT NULL CONSTRAINT DF_vehicles_mitronCapacity DEFAULT (500),
        mitronEfficiency FLOAT NOT NULL CONSTRAINT DF_vehicles_mitronEfficiency DEFAULT (0)
    );
    CREATE INDEX IX_vehicles_CharID ON dbo.vehicles(CharID);
END;

IF OBJECT_ID(N'dbo.items', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.items
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_items PRIMARY KEY,
        CharacterId BIGINT NULL,
        InventoryIndex INT NULL,
        StackNum BIGINT NULL CONSTRAINT DF_items_StackNum DEFAULT (1),
        CarId BIGINT NULL,
        Durability REAL NULL CONSTRAINT DF_items_Durability DEFAULT (100),
        Slot INT NULL CONSTRAINT DF_items_Slot DEFAULT (0),
        TableIndex INT NULL,
        Random INT NULL CONSTRAINT DF_items_Random DEFAULT (0),
        UpgradePoint INT NULL CONSTRAINT DF_items_UpgradePoint DEFAULT (0),
        Upgrade INT NULL CONSTRAINT DF_items_Upgrade DEFAULT (0),
        Belonging BIGINT NULL CONSTRAINT DF_items_Belonging DEFAULT (0),
        Box BIGINT NULL CONSTRAINT DF_items_Box DEFAULT (0),
        AssistJ BIGINT NULL CONSTRAINT DF_items_AssistJ DEFAULT (0),
        AssistI BIGINT NULL CONSTRAINT DF_items_AssistI DEFAULT (0),
        AssistH BIGINT NULL CONSTRAINT DF_items_AssistH DEFAULT (0),
        AssistG BIGINT NULL CONSTRAINT DF_items_AssistG DEFAULT (0),
        AssistF BIGINT NULL CONSTRAINT DF_items_AssistF DEFAULT (0),
        AssistE BIGINT NULL CONSTRAINT DF_items_AssistE DEFAULT (0),
        AssistD BIGINT NULL CONSTRAINT DF_items_AssistD DEFAULT (0),
        AssistC BIGINT NULL CONSTRAINT DF_items_AssistC DEFAULT (0),
        AssistB BIGINT NULL CONSTRAINT DF_items_AssistB DEFAULT (0),
        AssistA BIGINT NULL CONSTRAINT DF_items_AssistA DEFAULT (0),
        State INT NULL CONSTRAINT DF_items_State DEFAULT (0)
    );
    CREATE INDEX IX_items_CharacterId ON dbo.items(CharacterId);
END;

IF OBJECT_ID(N'dbo.friends', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.friends
    (
        SERVERID INT NOT NULL CONSTRAINT DF_friends_SERVERID DEFAULT (0),
        CID BIGINT NOT NULL,
        FCID BIGINT NOT NULL,
        FSTATE CHAR(1) NULL CONSTRAINT DF_friends_FSTATE DEFAULT ('F'),
        CONSTRAINT PK_friends PRIMARY KEY (SERVERID, CID, FCID)
    );
END;

IF OBJECT_ID(N'dbo.quests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.quests
    (
        ServerId INT NOT NULL,
        CID BIGINT NOT NULL,
        CNAME VARCHAR(32) NOT NULL,
        QuestId BIGINT NOT NULL,
        State INT NOT NULL,
        FailNum INT NOT NULL,
        PlaceIdx INT NOT NULL,
        LastDate BIGINT NULL,
        CONSTRAINT PK_quests PRIMARY KEY (ServerId, CID, QuestId)
    );
END;

IF OBJECT_ID(N'dbo.servers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.servers
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_servers PRIMARY KEY,
        Name VARCHAR(255) NOT NULL CONSTRAINT DF_servers_Name DEFAULT ('Test'),
        PlayersOnline INT NULL CONSTRAINT DF_servers_PlayersOnline DEFAULT (0),
        MaxPlayers INT NULL CONSTRAINT DF_servers_MaxPlayers DEFAULT (10),
        GameServerIp VARCHAR(255) NULL CONSTRAINT DF_servers_GameServerIp DEFAULT ('127.0.0.1'),
        GameServerPort INT NULL CONSTRAINT DF_servers_GameServerPort DEFAULT (11021),
        LobbyServerIp VARCHAR(255) NULL CONSTRAINT DF_servers_LobbyServerIp DEFAULT ('127.0.0.1'),
        LobbyServerPort INT NULL CONSTRAINT DF_servers_LobbyServerPort DEFAULT (11011),
        AreaServer1Ip VARCHAR(255) NULL CONSTRAINT DF_servers_AreaServer1Ip DEFAULT ('127.0.0.1'),
        AreaServer1UdpPort INT NULL CONSTRAINT DF_servers_AreaServer1UdpPort DEFAULT (10701),
        AreaServer1Port INT NULL CONSTRAINT DF_servers_AreaServer1Port DEFAULT (11031),
        AreaServer2Ip VARCHAR(255) NULL CONSTRAINT DF_servers_AreaServer2Ip DEFAULT ('127.0.0.1'),
        AreaServer2UdpPort INT NULL CONSTRAINT DF_servers_AreaServer2UdpPort DEFAULT (10702),
        AreaServer2Port INT NULL CONSTRAINT DF_servers_AreaServer2Port DEFAULT (11041),
        RankingServerIp VARCHAR(255) NULL CONSTRAINT DF_servers_RankingServerIp DEFAULT ('127.0.0.1'),
        RankingServerPort INT NULL CONSTRAINT DF_servers_RankingServerPort DEFAULT (11078)
    );
END;

IF OBJECT_ID(N'dbo.shop', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.shop
    (
        ItemID BIGINT NOT NULL,
        Price INT NOT NULL
    );
    CREATE UNIQUE INDEX UX_shop_ItemID ON dbo.shop(ItemID);
END;

IF OBJECT_ID(N'dbo.teams', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.teams
    (
        SERVERID INT NOT NULL CONSTRAINT DF_teams_SERVERID DEFAULT (0),
        TID BIGINT IDENTITY(1,1) NOT NULL,
        TMARKID BIGINT NULL CONSTRAINT DF_teams_TMARKID DEFAULT (-1),
        TEAMNAME VARCHAR(16) NOT NULL,
        UTEAMNAME VARCHAR(16) NOT NULL,
        TEAMDESC VARCHAR(80) NULL,
        TEAMLEVEL BIGINT NULL CONSTRAINT DF_teams_TEAMLEVEL DEFAULT (0),
        TEAMPOINT BIGINT NULL CONSTRAINT DF_teams_TEAMPOINT DEFAULT (0),
        TEAMRANKING BIGINT NULL CONSTRAINT DF_teams_TEAMRANKING DEFAULT (0),
        LEFTNEXP BIGINT NULL CONSTRAINT DF_teams_LEFTNEXP DEFAULT (0),
        LEFTPLAYTIME BIGINT NULL CONSTRAINT DF_teams_LEFTPLAYTIME DEFAULT (0),
        LEFTITEMVAL BIGINT NULL CONSTRAINT DF_teams_LEFTITEMVAL DEFAULT (0),
        CHANNELWINCNT BIGINT NULL CONSTRAINT DF_teams_CHANNELWINCNT DEFAULT (0),
        MEMBERCNT BIGINT NULL CONSTRAINT DF_teams_MEMBERCNT DEFAULT (0),
        TEAMGRADE CHAR(1) NULL,
        TEAMTOTALPOINT BIGINT NULL,
        TAXINCOME BIGINT NULL CONSTRAINT DF_teams_TAXINCOME DEFAULT (0),
        CID BIGINT NOT NULL,
        CNAME VARCHAR(32) NOT NULL,
        OWNCHANNEL VARCHAR(40) NULL,
        TEAMSTATE CHAR(1) NULL CONSTRAINT DF_teams_TEAMSTATE DEFAULT ('A'),
        CREATEDATE BIGINT NULL CONSTRAINT DF_teams_CREATEDATE DEFAULT (0),
        CLOSEDATE BIGINT NULL CONSTRAINT DF_teams_CLOSEDATE DEFAULT (0),
        BANISHDATE BIGINT NULL CONSTRAINT DF_teams_BANISHDATE DEFAULT (0),
        TEAMURL VARCHAR(32) NULL,
        UTEAMURL VARCHAR(32) NULL,
        LASTDATE BIGINT NULL,
        CONSTRAINT PK_teams PRIMARY KEY (TID, SERVERID)
    );
    CREATE UNIQUE INDEX UX_teams_TEAMNAME ON dbo.teams(TEAMNAME);
END;

IF OBJECT_ID(N'dbo.updates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.updates
    (
        path VARCHAR(255) NOT NULL CONSTRAINT PK_updates PRIMARY KEY
    );
END;
";
    }
}
