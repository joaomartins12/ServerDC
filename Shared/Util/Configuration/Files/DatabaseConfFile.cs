namespace Shared.Util.Configuration.Files
{
    /// <summary>
    /// Represents system/conf/database.conf for Microsoft SQL Server.
    /// Leave user/pass empty to use Windows Authentication.
    /// </summary>
    public class DatabaseConfFile : ConfFile
    {
        public string Host { get; protected set; }
        public int Port { get; protected set; }
        public string User { get; protected set; }
        public string Pass { get; protected set; }
        public string Db { get; protected set; }

        public void Load()
        {
            Require("system/conf/database.conf");

            Host = GetString("host", "localhost");
            Port = GetInt("port", 1433);
            User = GetString("user", "");
            Pass = GetString("pass", "");
            Db = GetString("database", "DCServer");
        }
    }
}
