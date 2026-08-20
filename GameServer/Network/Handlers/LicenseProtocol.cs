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
    /// The public DCNC parser database names 811/812, 813/814, 815/816 and 817,
    /// but no public implementation documents the exact ACK record layouts. We keep
    /// all requests observable and persist safe operations without fabricating large
    /// client structures. Packet 817 (NewLicenseNoti) is the only push whose semantic
    /// payload is unambiguous enough to bootstrap the default Rookie license: one id.
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
                WriteLog("GET_INFO no-character request=" + Hex(request));
                return;
            }

            using (var connection = GameServer.Instance.Database.Connection)
            {
                EnsureRookie(connection, character);
                var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var unlocked = CharacterProgressModel.GetUnlockedLicenses(connection, character.Id);
                var catalogCount = GetCatalogCount(connection);

                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "GET_INFO cid={0} name={1} request={2} current={3} unlocked=[{4}] catalogCount={5}",
                    character.Id,
                    character.Name,
                    Hex(request),
                    current,
                    string.Join(",", unlocked),
                    catalogCount));
            }
        }

        [Packet(CmdGetLicenseCond)]
        public static void GetLicenseCond(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                WriteLog("GET_COND no-character request=" + Hex(request));
                return;
            }

            using (var connection = GameServer.Instance.Database.Connection)
            {
                EnsureRookie(connection, character);
                var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var progressRows = CountProgressRows(connection, character.Id);
                var requirementRows = GetRequirementCount(connection);

                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "GET_COND cid={0} name={1} request={2} requestU32={3} current={4} progressRows={5} catalogRequirements={6}",
                    character.Id,
                    character.Name,
                    Hex(request),
                    request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0u,
                    current,
                    progressRows,
                    requirementRows));
            }
        }

        [Packet(CmdSelectLicense)]
        public static void SelectLicense(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                WriteLog("SELECT no-character request=" + Hex(request));
                return;
            }

            var candidate = FindLicenseId(request);
            using (var connection = GameServer.Instance.Database.Connection)
            {
                EnsureRookie(connection, character);
                var before = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var changed = false;

                if (candidate >= 7000 && candidate < 8000 && CharacterProgressModel.HasLicense(connection, character.Id, candidate))
                    changed = CharacterProgressModel.SetCurrentLicense(connection, character.Id, candidate);

                var after = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                WriteLog(string.Format(CultureInfo.InvariantCulture,
                    "SELECT cid={0} name={1} request={2} candidate={3} before={4} after={5} changed={6}",
                    character.Id,
                    character.Name,
                    Hex(request),
                    candidate,
                    before,
                    after,
                    changed));

                if (changed)
                    SendCurrentLicenseNotification(packet.Sender, after, "select");
            }
        }

        /// <summary>
        /// Called after the character is fully loaded. It guarantees the DB default and
        /// announces the equipped license through the protocol's dedicated new-license
        /// notification. This is intentionally small and logged so the v0.77a client
        /// behavior can be confirmed before 812/814/816 layouts are implemented.
        /// </summary>
        public static void Bootstrap(Client client, Character character)
        {
            if (client == null || character == null) return;

            try
            {
                using (var connection = GameServer.Instance.Database.Connection)
                {
                    EnsureRookie(connection, character);
                    var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                    var unlocked = CharacterProgressModel.GetUnlockedLicenses(connection, character.Id);

                    SendCurrentLicenseNotification(client, current, "login");
                    WriteLog(string.Format(CultureInfo.InvariantCulture,
                        "BOOTSTRAP cid={0} name={1} serial={2} current={3} unlocked=[{4}]",
                        character.Id,
                        character.Name,
                        client.User == null ? 0 : client.User.VehicleSerial,
                        current,
                        string.Join(",", unlocked)));
                }
            }
            catch (Exception ex)
            {
                WriteLog("BOOTSTRAP_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EnsureRookie(MySqlConnection connection, Character character)
        {
            if (!CharacterProgressModel.HasLicense(connection, character.Id, RookieLicenseId))
                CharacterProgressModel.UnlockLicense(connection, character.Id, RookieLicenseId, 0);

            var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
            if (current <= 0 || !CharacterProgressModel.HasLicense(connection, character.Id, current))
                CharacterProgressModel.SetCurrentLicense(connection, character.Id, RookieLicenseId);
        }

        private static void SendCurrentLicenseNotification(Client client, int licenseId, string reason)
        {
            if (client == null || licenseId <= 0) return;

            var notification = new Packet(NewLicenseNotification);
            notification.Writer.Write(licenseId);
            client.Send(notification);
            WriteLog("OUT 817 NewLicenseNoti license=" + licenseId + " reason=" + reason);
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

        private static string Hex(byte[] data)
        {
            if (data == null || data.Length == 0) return "<empty>";
            return BitConverter.ToString(data).Replace('-', ' ');
        }

        private static void WriteLog(string text)
        {
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
