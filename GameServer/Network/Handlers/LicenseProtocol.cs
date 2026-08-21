using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Shared.Models;
using Shared.Network;
using Shared.Objects;
using Shared.Util;

namespace GameServer.Network.Handlers
{
    /// <summary>
    /// License/title protocol research and persistence bridge for v0.77a.
    ///
    /// Known protocol family:
    /// 666 LicenseStatus / 667 LicenseStatusAck / 668 LCondStatusAck
    /// 806 LicenseInfoRes
    /// 811 GetLicenseInfo / 812 GetLicenseInfoAck
    /// 813 GetLicenseCond / 814 GetLicenseCondAck
    /// 815 SelectLicense / 816 SelectLicenseAck
    /// 817 NewLicenseNoti
    ///
    /// The exact record layouts for 667/668/806/812/814/816 are not public in DCNC.
    /// Until they are recovered, this handler keeps all requests observable and persists
    /// safe state without fabricating client structures.
    /// </summary>
    public static class LicenseProtocol
    {
        private const ushort CmdGetLicenseInfo = 811;
        private const ushort CmdGetLicenseCond = 813;
        private const ushort CmdSelectLicense = 815;
        private const ushort NewLicenseNotification = 817;
        private const int RookieLicenseId = 7000;

        [Packet(CmdGetLicenseInfo)]
        public static void GetLicenseInfo(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                WriteLog("GET_INFO no-character request=" + Hex(request), true);
                return;
            }

            try
            {
                // IMPORTANT: Database.Connection belongs to GameServer. Do not dispose it here.
                var connection = GameServer.Instance.Database.Connection;
                EnsureRookie(connection, character);

                var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var unlocked = CharacterProgressModel.GetUnlockedLicenses(connection, character.Id);
                var catalogCount = GetCatalogCount(connection);
                var rookie = GetLicenseCatalogSummary(connection, RookieLicenseId);

                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "GET_INFO cid={0} name={1} requestLen={2} request={3} requestU32={4} current={5} unlocked=[{6}] unlockedCount={7} catalogCount={8} rookie={9} ACK812=NOT_SENT_LAYOUT_UNKNOWN",
                    character.Id,
                    character.Name,
                    request.Length,
                    Hex(request),
                    request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0u,
                    current,
                    string.Join(",", unlocked),
                    unlocked.Count,
                    catalogCount,
                    rookie));
            }
            catch (Exception ex)
            {
                WriteLog("GET_INFO_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message, true);
            }
        }

        [Packet(CmdGetLicenseCond)]
        public static void GetLicenseCond(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                WriteLog("GET_COND no-character request=" + Hex(request), true);
                return;
            }

            try
            {
                var connection = GameServer.Instance.Database.Connection;
                EnsureRookie(connection, character);

                var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var unlocked = CharacterProgressModel.GetUnlockedLicenses(connection, character.Id);
                var progressRows = CountProgressRows(connection, character.Id);
                var requirementRows = GetRequirementCount(connection);
                var rookieRequirements = GetLicenseRequirementSummary(connection, RookieLicenseId);

                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "GET_COND cid={0} name={1} requestLen={2} request={3} requestU32={4} current={5} unlocked=[{6}] progressRows={7} catalogRequirements={8} rookieRequirements={9} ACK814=NOT_SENT_LAYOUT_UNKNOWN",
                    character.Id,
                    character.Name,
                    request.Length,
                    Hex(request),
                    request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0u,
                    current,
                    string.Join(",", unlocked),
                    progressRows,
                    requirementRows,
                    rookieRequirements));
            }
            catch (Exception ex)
            {
                WriteLog("GET_COND_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message, true);
            }
        }

        [Packet(CmdSelectLicense)]
        public static void SelectLicense(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                WriteLog("SELECT no-character request=" + Hex(request), true);
                return;
            }

            try
            {
                var candidate = FindLicenseId(request);
                var connection = GameServer.Instance.Database.Connection;
                EnsureRookie(connection, character);

                var before = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var changed = false;
                var owned = candidate >= 7000 && candidate < 8000 &&
                            CharacterProgressModel.HasLicense(connection, character.Id, candidate);

                if (owned)
                    changed = CharacterProgressModel.SetCurrentLicense(connection, character.Id, candidate);

                var after = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "SELECT cid={0} name={1} requestLen={2} request={3} candidate={4} owned={5} before={6} after={7} changed={8} ACK816=NOT_SENT_LAYOUT_UNKNOWN",
                    character.Id,
                    character.Name,
                    request.Length,
                    Hex(request),
                    candidate,
                    owned,
                    before,
                    after,
                    changed));

                if (changed)
                    SendCurrentLicenseNotification(packet.Sender, after, "select");
            }
            catch (Exception ex)
            {
                WriteLog("SELECT_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message, true);
            }
        }

        /// <summary>
        /// Called after the character is fully loaded. Guarantees Rookie ownership and
        /// announces the current license through 817. 817 is a notification only; it is
        /// not treated as a replacement for the missing state ACKs.
        /// </summary>
        public static void Bootstrap(Client client, Character character)
        {
            if (client == null || character == null) return;

            try
            {
                var connection = GameServer.Instance.Database.Connection;
                EnsureRookie(connection, character);

                var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var unlocked = CharacterProgressModel.GetUnlockedLicenses(connection, character.Id);
                var catalogCount = GetCatalogCount(connection);
                var rookie = GetLicenseCatalogSummary(connection, RookieLicenseId);
                var rookieRequirements = GetLicenseRequirementSummary(connection, RookieLicenseId);

                SendCurrentLicenseNotification(client, current, "login");
                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "BOOTSTRAP cid={0} name={1} serial={2} current={3} unlocked=[{4}] unlockedCount={5} catalogCount={6} rookie={7} rookieRequirements={8}",
                    character.Id,
                    character.Name,
                    client.User == null ? 0 : client.User.VehicleSerial,
                    current,
                    string.Join(",", unlocked),
                    unlocked.Count,
                    catalogCount,
                    rookie,
                    rookieRequirements));
            }
            catch (Exception ex)
            {
                WriteLog("BOOTSTRAP_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message, true);
            }
        }

        private static void EnsureRookie(MySqlConnection connection, Character character)
        {
            var hadRookie = CharacterProgressModel.HasLicense(connection, character.Id, RookieLicenseId);
            if (!hadRookie)
            {
                CharacterProgressModel.UnlockLicense(connection, character.Id, RookieLicenseId, 0);
                WriteLog("ENSURE_ROOKIE unlocked cid=" + character.Id + " license=" + RookieLicenseId);
            }

            var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
            if (current <= 0 || !CharacterProgressModel.HasLicense(connection, character.Id, current))
            {
                CharacterProgressModel.SetCurrentLicense(connection, character.Id, RookieLicenseId);
                WriteLog("ENSURE_ROOKIE equipped-default cid=" + character.Id + " previous=" + current + " current=" + RookieLicenseId);
            }
        }

        private static void SendCurrentLicenseNotification(Client client, int licenseId, string reason)
        {
            if (client == null || licenseId <= 0) return;

            var notification = new Packet(NewLicenseNotification);
            notification.Writer.Write(licenseId);
            client.Send(notification);
            WriteLog("OUT 817 NewLicenseNoti license=" + licenseId + " reason=" + reason + " payloadU32=" + licenseId);
        }

        private static int FindLicenseId(byte[] request)
        {
            if (request == null) return 0;
            for (var offset = 0; offset + 4 <= request.Length; offset += 2)
            {
                var value = BitConverter.ToInt32(request, offset);
                if (value >= 7000 && value < 8000) return value;
            }
            for (var offset = 0; offset + 2 <= request.Length; offset += 2)
            {
                var value = BitConverter.ToUInt16(request, offset);
                if (value >= 7000 && value < 8000) return value;
            }
            return 0;
        }

        private static byte[] ReadRemaining(Packet packet)
        {
            var stream = packet.Reader.BaseStream;
            var remaining = (int)Math.Max(0, stream.Length - stream.Position);
            return remaining == 0 ? new byte[0] : packet.Reader.ReadBytes(remaining);
        }

        private static int GetCatalogCount(MySqlConnection connection)
        {
            using (var command = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.license_catalog', N'U') IS NULL SELECT CAST(0 AS INT)
ELSE SELECT COUNT(1) FROM dbo.license_catalog;", connection))
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static int GetRequirementCount(MySqlConnection connection)
        {
            using (var command = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.license_requirements', N'U') IS NULL SELECT CAST(0 AS INT)
ELSE SELECT COUNT(1) FROM dbo.license_requirements;", connection))
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static int CountProgressRows(MySqlConnection connection, ulong cid)
        {
            using (var command = new MySqlCommand(
                "SELECT COUNT(1) FROM dbo.character_progress WHERE CID=@cid", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static string GetLicenseCatalogSummary(MySqlConnection connection, int licenseId)
        {
            try
            {
                using (var command = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.license_catalog', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS NVARCHAR(256)) AS Name, CAST(NULL AS NVARCHAR(128)) AS Category, CAST(NULL AS NVARCHAR(32)) AS Grade;
END
ELSE
BEGIN
    SELECT TOP 1 Name, Category, Grade FROM dbo.license_catalog WHERE LicenseId=@id;
END", connection))
                {
                    command.Parameters.AddWithValue("@id", licenseId);
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return "<missing>";
                        var name = reader.IsDBNull(0) ? "?" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                        var category = reader.IsDBNull(1) ? "?" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                        var grade = reader.IsDBNull(2) ? "?" : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
                        return name + "/cat=" + category + "/grade=" + grade;
                    }
                }
            }
            catch (Exception ex)
            {
                return "<catalog-error:" + ex.GetType().Name + ">";
            }
        }

        private static string GetLicenseRequirementSummary(MySqlConnection connection, int licenseId)
        {
            try
            {
                using (var command = new MySqlCommand(@"
IF OBJECT_ID(N'dbo.license_requirements', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS NVARCHAR(128)) AS RequirementKey, CAST(NULL AS BIGINT) AS RequirementValue WHERE 1=0;
END
ELSE
BEGIN
    SELECT RequirementKey, RequirementValue FROM dbo.license_requirements WHERE LicenseId=@id ORDER BY Slot;
END", connection))
                {
                    command.Parameters.AddWithValue("@id", licenseId);
                    var values = new List<string>();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var key = reader.IsDBNull(0) ? "?" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                            var value = reader.IsDBNull(1) ? "?" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                            values.Add(key + ":" + value);
                        }
                    }
                    return values.Count == 0 ? "<none>" : string.Join(";", values);
                }
            }
            catch (Exception ex)
            {
                return "<requirements-error:" + ex.GetType().Name + ">";
            }
        }

        private static string Hex(byte[] data)
        {
            if (data == null || data.Length == 0) return "<empty>";
            return BitConverter.ToString(data).Replace('-', ' ');
        }

        private static void WriteLog(string text, bool warning = false)
        {
            // Always mirror license research into the normal server log so PacketCapture
            // ZIPs contain the protocol state without depending on process working folder.
            if (warning)
                Log.Warning("LicenseProtocol: {0}", text);
            else
                Log.Debug("LicenseProtocol: {0}", text);

            try
            {
                var dir = Path.Combine("Logs", "Research");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "LicenseProtocol.txt"),
                    DateTime.UtcNow.ToString("O") + " " + text + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
