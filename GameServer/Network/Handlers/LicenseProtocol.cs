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
    /// Drift City v0.77a license/title protocol.
    ///
    /// Packet layouts reconstructed from the actual v0.77a client:
    /// 806 = player serial (u16) + XiLicense (6 bytes)
    /// 812 = reward money (u32) + count (u32) + XiLicense[count]
    /// 814 = request key (u32) + count (u16) + page/reset (u16) + total (u32)
    ///       + XiLicenseCondition[count]
    /// 816 = XiLicense (6 bytes)
    /// 817 = XiLicense (6 bytes)
    ///
    /// XiLicense = LicenseId(u16), State(u16), Equipped(u16).
    /// XiLicenseCondition = ConditionId(u16), ProgressValue(u32).
    /// </summary>
    public static class LicenseProtocol
    {
        private const ushort LicenseInfoRes = 806;
        private const ushort CmdGetLicenseInfo = 811;
        private const ushort CmdGetLicenseInfoAck = 812;
        private const ushort CmdGetLicenseCond = 813;
        private const ushort CmdGetLicenseCondAck = 814;
        private const ushort CmdSelectLicense = 815;
        private const ushort CmdSelectLicenseAck = 816;
        private const ushort NewLicenseNotification = 817;
        private const int RookieLicenseId = 7000;

        private sealed class LicenseConditionProgress
        {
            public ushort ConditionId;
            public uint Progress;
            public string Key;
        }

        [Packet(CmdGetLicenseInfo)]
        public static void GetLicenseInfo(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                Research("GET_INFO no-character request=" + Hex(request));
                return;
            }

            try
            {
                var connection = GameServer.Instance.Database.Connection;
                EnsureRookie(connection, character);

                var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var unlocked = CharacterProgressModel.GetUnlockedLicenses(connection, character.Id);
                var catalogCount = GetCatalogCount(connection);

                SendLicenseInfoAck(packet.Sender, current, unlocked, 0, "get-info");
                SendEquippedLicenseInfo(packet.Sender, current, "get-info");

                Research(string.Format(CultureInfo.InvariantCulture,
                    "GET_INFO cid={0} name={1} request={2} current={3} unlocked=[{4}] catalogCount={5} -> 812+806",
                    character.Id, character.Name, Hex(request), current,
                    string.Join(",", unlocked), catalogCount));
            }
            catch (Exception ex)
            {
                Research("GET_INFO_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        [Packet(CmdGetLicenseCond)]
        public static void GetLicenseCond(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                Research("GET_COND no-character request=" + Hex(request));
                return;
            }

            try
            {
                var connection = GameServer.Instance.Database.Connection;
                EnsureRookie(connection, character);

                var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var requestKey = request.Length >= 4 ? BitConverter.ToUInt32(request, 0) : 0u;
                var conditions = LoadConditionProgress(connection, character.Id, current);

                SendLicenseCondAck(packet.Sender, requestKey, conditions, "get-cond");

                Research(string.Format(CultureInfo.InvariantCulture,
                    "GET_COND cid={0} name={1} request={2} requestU32=0x{3:X8} current={4} conds={5} [{6}] -> 814",
                    character.Id, character.Name, Hex(request), requestKey, current,
                    conditions.Count,
                    string.Join(",", conditions.Select(x => x.Key + "#" + x.ConditionId + "=" + x.Progress))));
            }
            catch (Exception ex)
            {
                Research("GET_COND_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        [Packet(CmdSelectLicense)]
        public static void SelectLicense(Packet packet)
        {
            var request = ReadRemaining(packet);
            var character = packet.Sender?.User?.ActiveCharacter;
            if (character == null)
            {
                Research("SELECT no-character request=" + Hex(request));
                return;
            }

            try
            {
                var candidate = FindLicenseId(request);
                var connection = GameServer.Instance.Database.Connection;
                EnsureRookie(connection, character);

                var before = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                var changed = false;

                if (candidate >= 7000 && candidate < 8000 &&
                    CharacterProgressModel.HasLicense(connection, character.Id, candidate))
                {
                    changed = CharacterProgressModel.SetCurrentLicense(connection, character.Id, candidate);
                }

                var after = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
                if (after <= 0) after = RookieLicenseId;

                SendSelectLicenseAck(packet.Sender, after, "select");
                SendEquippedLicenseInfo(packet.Sender, after, "select");

                Research(string.Format(CultureInfo.InvariantCulture,
                    "SELECT cid={0} name={1} request={2} candidate={3} before={4} after={5} changed={6} -> 816+806",
                    character.Id, character.Name, Hex(request), candidate, before, after, changed));
            }
            catch (Exception ex)
            {
                Research("SELECT_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Restores complete license state after the character is loaded.
        /// 812 marks ownership, 816 applies the currently equipped title locally,
        /// 806 applies it to the player object and 817 notifies the client of ownership.
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
                if (current <= 0 || !unlocked.Contains(current))
                {
                    current = RookieLicenseId;
                    CharacterProgressModel.SetCurrentLicense(connection, character.Id, current);
                }

                SendLicenseInfoAck(client, current, unlocked, 0, "login");

                // Critical: the v0.77a client only actually equips/restores the local title
                // after processing packet 816. Packet 806 alone updates player license data
                // but does not perform the local Equip License transition.
                SendSelectLicenseAck(client, current, "login-restore");
                SendEquippedLicenseInfo(client, current, "login");
                SendCurrentLicenseNotification(client, current, "login");

                Research(string.Format(CultureInfo.InvariantCulture,
                    "BOOTSTRAP cid={0} name={1} serial={2} current={3} unlocked=[{4}] -> 812+816+806+817",
                    character.Id,
                    character.Name,
                    client.User == null ? 0 : client.User.VehicleSerial,
                    current,
                    string.Join(",", unlocked)));
            }
            catch (Exception ex)
            {
                Research("BOOTSTRAP_ERROR cid=" + character.Id + " " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EnsureRookie(MySqlConnection connection, Character character)
        {
            CharacterModel.EnsureDefaultLicense(connection, character.Id);

            if (!CharacterProgressModel.HasLicense(connection, character.Id, RookieLicenseId))
            {
                CharacterProgressModel.UnlockLicense(connection, character.Id, RookieLicenseId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Research("UNLOCK_DEFAULT cid=" + character.Id + " license=" + RookieLicenseId);
            }

            var current = CharacterProgressModel.GetCurrentLicense(connection, character.Id);
            if (current <= 0 || !CharacterProgressModel.HasLicense(connection, character.Id, current))
            {
                CharacterProgressModel.SetCurrentLicense(connection, character.Id, RookieLicenseId);
                Research("EQUIP_DEFAULT cid=" + character.Id + " license=" + RookieLicenseId);
            }
        }

        private static void WriteXiLicense(Packet packet, int licenseId, bool equipped)
        {
            packet.Writer.Write((ushort)licenseId);
            packet.Writer.Write((ushort)0);
            packet.Writer.Write((ushort)(equipped ? 1 : 0));
        }

        private static void SendLicenseInfoAck(Client client, int currentLicenseId,
            IList<int> unlocked, uint rewardMoney, string reason)
        {
            if (client == null) return;

            var list = (unlocked ?? new List<int>())
                .Where(x => x >= 7000 && x < 8000)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (!list.Contains(RookieLicenseId)) list.Insert(0, RookieLicenseId);

            var ack = new Packet(CmdGetLicenseInfoAck);
            ack.Writer.Write(rewardMoney);
            ack.Writer.Write((uint)list.Count);
            foreach (var licenseId in list)
                WriteXiLicense(ack, licenseId, licenseId == currentLicenseId);

            client.Send(ack);
            Research("OUT 812 GetLicenseInfoAck reward=" + rewardMoney +
                " count=" + list.Count + " current=" + currentLicenseId +
                " entries=[" + string.Join(",", list) + "] reason=" + reason);
        }

        private static void SendLicenseCondAck(Client client, uint requestKey,
            IList<LicenseConditionProgress> conditions, string reason)
        {
            if (client == null) return;
            var list = conditions == null ? new List<LicenseConditionProgress>() : conditions.ToList();

            var ack = new Packet(CmdGetLicenseCondAck);
            ack.Writer.Write(requestKey);
            ack.Writer.Write((ushort)list.Count);
            ack.Writer.Write((ushort)0);
            ack.Writer.Write((uint)list.Count);

            foreach (var condition in list)
            {
                ack.Writer.Write(condition.ConditionId);
                ack.Writer.Write(condition.Progress);
            }

            client.Send(ack);
            Research("OUT 814 GetLicenseCondAck request=0x" + requestKey.ToString("X8") +
                " count=" + list.Count + " total=" + list.Count + " reason=" + reason);
        }

        private static void SendSelectLicenseAck(Client client, int licenseId, string reason)
        {
            if (client == null || licenseId <= 0) return;
            var ack = new Packet(CmdSelectLicenseAck);
            WriteXiLicense(ack, licenseId, true);
            client.Send(ack);
            Research("OUT 816 SelectLicenseAck license=" + licenseId + " reason=" + reason);
        }

        private static void SendEquippedLicenseInfo(Client client, int licenseId, string reason)
        {
            if (client == null || client.User == null || licenseId <= 0) return;

            var serial = client.User.VehicleSerial;
            if (serial <= 0 || serial > ushort.MaxValue)
            {
                Research("OUT 806 SKIP invalid-serial=" + serial + " license=" + licenseId + " reason=" + reason);
                return;
            }

            var packet = new Packet(LicenseInfoRes);
            packet.Writer.Write((ushort)serial);
            WriteXiLicense(packet, licenseId, true);
            client.Send(packet);

            Research("OUT 806 LicenseInfoRes serial=" + serial + " license=" + licenseId + " reason=" + reason);
        }

        private static void SendCurrentLicenseNotification(Client client, int licenseId, string reason)
        {
            if (client == null || licenseId <= 0) return;

            var notification = new Packet(NewLicenseNotification);
            WriteXiLicense(notification, licenseId, true);
            client.Send(notification);
            Research("OUT 817 NewLicenseNoti license=" + licenseId + " reason=" + reason);
        }

        private static List<LicenseConditionProgress> LoadConditionProgress(
            MySqlConnection connection, ulong cid, int licenseId)
        {
            var result = new List<LicenseConditionProgress>();
            using (var command = new MySqlCommand(@"
SELECT
    r.RequirementKey,
    CASE WHEN c.SourceRow IS NULL OR c.SourceRow < 3 THEN 0 ELSE c.SourceRow - 3 END AS ConditionId,
    COALESCE(p.ProgressValue, 0) AS ProgressValue
FROM dbo.license_requirements r
LEFT JOIN dbo.license_condition_catalog c
    ON c.ConditionKey = r.RequirementKey
LEFT JOIN dbo.character_progress p
    ON p.CID = @cid AND p.ProgressKey = r.RequirementKey
WHERE r.LicenseId = @license
ORDER BY r.Slot ASC;", connection))
            {
                command.Parameters.AddWithValue("@cid", cid);
                command.Parameters.AddWithValue("@license", licenseId);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var conditionId = Convert.ToInt32(reader["ConditionId"], CultureInfo.InvariantCulture);
                        var progress = Convert.ToInt64(reader["ProgressValue"], CultureInfo.InvariantCulture);
                        if (conditionId < 0 || conditionId > ushort.MaxValue) continue;
                        if (progress < 0) progress = 0;
                        if (progress > uint.MaxValue) progress = uint.MaxValue;

                        result.Add(new LicenseConditionProgress
                        {
                            ConditionId = (ushort)conditionId,
                            Progress = (uint)progress,
                            Key = Convert.ToString(reader["RequirementKey"], CultureInfo.InvariantCulture) ?? string.Empty
                        });
                    }
                }
            }
            return result;
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

        private static string Hex(byte[] data)
        {
            if (data == null || data.Length == 0) return "<empty>";
            return BitConverter.ToString(data).Replace('-', ' ');
        }

        private static void Research(string text)
        {
            Log.Info("LicenseProtocol: {0}", text);
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
