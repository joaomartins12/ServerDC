using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ServerManager
{
    /// <summary>
    /// Imports the three client-side license tables and preserves every original XLT cell
    /// before deriving normalized license / requirement / effect catalogs for server-side
    /// validation. The XLT container uses the same offset/string table layout as the other
    /// Drift City client tables handled by ClientDataImporter.
    /// </summary>
    internal static class LicenseDataImporter
    {
        private static readonly string[] RequiredFileNames =
        {
            "License.xlt",
            "License_GC.xlt",
            "License_ME.xlt"
        };

        internal sealed class ImportResult
        {
            public int Files;
            public int SourceRows;
            public int SourceCells;
            public int Licenses;
            public int Requirements;
            public int Effects;
            public string Folder;
        }

        private sealed class XltSnapshot
        {
            public string FileName;
            public string FullPath;
            public string Hash;
            public int HeaderBytes;
            public short VersionMajor;
            public short VersionMinor;
            public short SourceYear;
            public byte SourceMonth;
            public byte SourceDay;
            public uint Flag;
            public uint HeaderOffset;
            public int ColumnCount;
            public int RowCount;
            public readonly List<string[]> Rows = new List<string[]>();
        }

        public static string ImportFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LicenseImporter"); }
        }

        public static string EnsureImportDirectory()
        {
            Directory.CreateDirectory(ImportFolder);
            return ImportFolder;
        }

        public static string[] GetMissingRequiredFiles()
        {
            var folder = EnsureImportDirectory();
            return RequiredFileNames
                .Where(name => !File.Exists(Path.Combine(folder, name)))
                .ToArray();
        }

        public static int CountRequiredFiles()
        {
            return RequiredFileNames.Length - GetMissingRequiredFiles().Length;
        }

        public static ImportResult ImportAll()
        {
            var folder = EnsureImportDirectory();
            var missing = GetMissingRequiredFiles();
            if (missing.Length != 0)
                throw new InvalidOperationException("Missing required XLT files: " + string.Join(", ", missing));

            var snapshots = RequiredFileNames
                .Select(name => Parse(Path.Combine(folder, name)))
                .ToDictionary(x => x.FileName, StringComparer.OrdinalIgnoreCase);

            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = "DCServer",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity License XLT Importer"
            }.ConnectionString;

            var sourceRows = 0;
            var sourceCells = 0;
            var licenses = 0;
            var requirements = 0;
            var effects = 0;

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var tx = connection.BeginTransaction())
                {
                    try
                    {
                        EnsureSchema(connection, tx);
                        ClearImportedData(connection, tx);

                        foreach (var snapshot in snapshots.Values)
                        {
                            InsertSourceFile(connection, tx, snapshot);
                            sourceRows += snapshot.RowCount;
                            sourceCells += InsertSourceCells(connection, tx, snapshot);
                        }

                        XltSnapshot licenseTable;
                        if (snapshots.TryGetValue("License.xlt", out licenseTable))
                        {
                            licenses = InsertLicenseCatalog(connection, tx, licenseTable);
                            requirements = InsertRequirementCatalog(connection, tx, licenseTable);
                            effects = InsertEffectCatalog(connection, tx, licenseTable);
                        }

                        XltSnapshot gcTable;
                        if (snapshots.TryGetValue("License_GC.xlt", out gcTable))
                            InsertKeyCatalog(connection, tx, gcTable, "GC_", "dbo.license_condition_catalog", "ConditionKey");

                        XltSnapshot meTable;
                        if (snapshots.TryGetValue("License_ME.xlt", out meTable))
                            InsertKeyCatalog(connection, tx, meTable, "ME_", "dbo.license_effect_catalog", "EffectKey");

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }

            return new ImportResult
            {
                Files = snapshots.Count,
                SourceRows = sourceRows,
                SourceCells = sourceCells,
                Licenses = licenses,
                Requirements = requirements,
                Effects = effects,
                Folder = folder
            };
        }

        private static XltSnapshot Parse(string path)
        {
            var file = File.ReadAllBytes(path);
            if (file.Length < 4)
                throw new InvalidDataException(Path.GetFileName(path) + " is too small to be an XLT table.");

            var headerBytes = BitConverter.ToUInt16(file, 2);
            if (headerBytes >= file.Length || file.Length - headerBytes < 24)
                throw new InvalidDataException(Path.GetFileName(path) + " has an invalid table header offset " + headerBytes + ".");

            var dataLength = file.Length - headerBytes;
            var data = new byte[dataLength];
            Buffer.BlockCopy(file, headerBytes, data, 0, dataLength);

            var snapshot = new XltSnapshot
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                Hash = Sha256(file),
                HeaderBytes = headerBytes,
                VersionMajor = BitConverter.ToInt16(data, 0),
                VersionMinor = BitConverter.ToInt16(data, 2),
                SourceYear = BitConverter.ToInt16(data, 4),
                SourceMonth = data[6],
                SourceDay = data[7],
                Flag = BitConverter.ToUInt32(data, 8),
                HeaderOffset = BitConverter.ToUInt32(data, 12),
                ColumnCount = checked((int)BitConverter.ToUInt32(data, 16)),
                RowCount = checked((int)BitConverter.ToUInt32(data, 20))
            };

            if (snapshot.ColumnCount <= 0 || snapshot.RowCount < 0 || snapshot.ColumnCount > 4096)
                throw new InvalidDataException(snapshot.FileName + " contains invalid dimensions.");

            var offsetBytes = checked((long)snapshot.ColumnCount * snapshot.RowCount * 4L);
            if (24L + offsetBytes > data.Length)
                throw new InvalidDataException(snapshot.FileName + " contains a truncated offset table.");

            var cursor = 24;
            for (var row = 0; row < snapshot.RowCount; row++)
            {
                var values = new string[snapshot.ColumnCount];
                for (var column = 0; column < snapshot.ColumnCount; column++)
                {
                    var stringOffset = BitConverter.ToUInt32(data, cursor);
                    cursor += 4;
                    values[column] = ReadUnicodeString(data, stringOffset, snapshot.FileName, row, column);
                }
                snapshot.Rows.Add(values);
            }

            return snapshot;
        }

        private static string ReadUnicodeString(byte[] data, uint offset, string fileName, int row, int column)
        {
            if (offset >= data.Length)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "{0}: invalid string offset {1} at row {2}, column {3}.", fileName, offset, row, column));

            var start = checked((int)offset);
            var end = start;
            while (end + 1 < data.Length)
            {
                if (data[end] == 0 && data[end + 1] == 0) break;
                end += 2;
            }

            if (end + 1 >= data.Length)
                throw new InvalidDataException(fileName + " contains an unterminated string.");

            return Encoding.Unicode.GetString(data, start, end - start);
        }

        private static void EnsureSchema(SqlConnection connection, SqlTransaction tx)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.license_source_files', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_source_files
    (
        FileName NVARCHAR(64) NOT NULL CONSTRAINT PK_license_source_files PRIMARY KEY,
        FileHash CHAR(64) NOT NULL,
        HeaderBytes INT NOT NULL,
        VersionMajor SMALLINT NOT NULL,
        VersionMinor SMALLINT NOT NULL,
        SourceYear SMALLINT NOT NULL,
        SourceMonth TINYINT NOT NULL,
        SourceDay TINYINT NOT NULL,
        Flag BIGINT NOT NULL,
        HeaderOffset BIGINT NOT NULL,
        ColumnCount INT NOT NULL,
        RowCount INT NOT NULL,
        ImportedAt DATETIME2 NOT NULL CONSTRAINT DF_license_source_files_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.license_source_cells', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_source_cells
    (
        FileName NVARCHAR(64) NOT NULL,
        RowIndex INT NOT NULL,
        ColumnIndex INT NOT NULL,
        CellValue NVARCHAR(MAX) NULL,
        CONSTRAINT PK_license_source_cells PRIMARY KEY (FileName, RowIndex, ColumnIndex)
    );
    CREATE INDEX IX_license_source_cells_ValuePrefix ON dbo.license_source_cells(FileName, RowIndex, ColumnIndex);
END;

IF OBJECT_ID(N'dbo.license_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_catalog
    (
        LicenseId INT NOT NULL CONSTRAINT PK_license_catalog PRIMARY KEY,
        SourceRow INT NOT NULL,
        Name NVARCHAR(256) NULL,
        Category NVARCHAR(128) NULL,
        Grade NVARCHAR(32) NULL,
        RawData NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL CONSTRAINT DF_license_catalog_UpdatedAt DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.license_requirements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_requirements
    (
        LicenseId INT NOT NULL,
        Slot INT NOT NULL,
        RequirementKey NVARCHAR(128) NOT NULL,
        RequirementValue BIGINT NULL,
        RequirementParam NVARCHAR(512) NULL,
        SourceColumn INT NOT NULL,
        RawData NVARCHAR(1024) NULL,
        CONSTRAINT PK_license_requirements PRIMARY KEY (LicenseId, Slot)
    );
    CREATE INDEX IX_license_requirements_Key ON dbo.license_requirements(RequirementKey);
END;

IF OBJECT_ID(N'dbo.license_effects', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_effects
    (
        LicenseId INT NOT NULL,
        Slot INT NOT NULL,
        EffectKey NVARCHAR(128) NOT NULL,
        EffectValue BIGINT NULL,
        SourceColumn INT NOT NULL,
        RawData NVARCHAR(1024) NULL,
        CONSTRAINT PK_license_effects PRIMARY KEY (LicenseId, Slot)
    );
END;

IF OBJECT_ID(N'dbo.license_condition_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_condition_catalog
    (
        ConditionKey NVARCHAR(128) NOT NULL CONSTRAINT PK_license_condition_catalog PRIMARY KEY,
        SourceRow INT NOT NULL,
        DisplayName NVARCHAR(256) NULL,
        RawData NVARCHAR(MAX) NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.license_effect_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_effect_catalog
    (
        EffectKey NVARCHAR(128) NOT NULL CONSTRAINT PK_license_effect_catalog PRIMARY KEY,
        SourceRow INT NOT NULL,
        DisplayName NVARCHAR(256) NULL,
        RawData NVARCHAR(MAX) NOT NULL
    );
END;";

            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.CommandTimeout = 120;
                cmd.ExecuteNonQuery();
            }
        }

        private static void ClearImportedData(SqlConnection connection, SqlTransaction tx)
        {
            const string sql = @"
DELETE FROM dbo.license_effects;
DELETE FROM dbo.license_requirements;
DELETE FROM dbo.license_catalog;
DELETE FROM dbo.license_condition_catalog;
DELETE FROM dbo.license_effect_catalog;
DELETE FROM dbo.license_source_cells;
DELETE FROM dbo.license_source_files;";
            using (var cmd = new SqlCommand(sql, connection, tx))
                cmd.ExecuteNonQuery();
        }

        private static void InsertSourceFile(SqlConnection connection, SqlTransaction tx, XltSnapshot snapshot)
        {
            const string sql = @"
INSERT INTO dbo.license_source_files
(FileName, FileHash, HeaderBytes, VersionMajor, VersionMinor, SourceYear, SourceMonth, SourceDay,
 Flag, HeaderOffset, ColumnCount, RowCount, ImportedAt)
VALUES
(@file, @hash, @header, @major, @minor, @year, @month, @day, @flag, @offset, @columns, @rows, SYSUTCDATETIME());";
            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@file", snapshot.FileName);
                cmd.Parameters.AddWithValue("@hash", snapshot.Hash);
                cmd.Parameters.AddWithValue("@header", snapshot.HeaderBytes);
                cmd.Parameters.AddWithValue("@major", snapshot.VersionMajor);
                cmd.Parameters.AddWithValue("@minor", snapshot.VersionMinor);
                cmd.Parameters.AddWithValue("@year", snapshot.SourceYear);
                cmd.Parameters.AddWithValue("@month", snapshot.SourceMonth);
                cmd.Parameters.AddWithValue("@day", snapshot.SourceDay);
                cmd.Parameters.AddWithValue("@flag", (long)snapshot.Flag);
                cmd.Parameters.AddWithValue("@offset", (long)snapshot.HeaderOffset);
                cmd.Parameters.AddWithValue("@columns", snapshot.ColumnCount);
                cmd.Parameters.AddWithValue("@rows", snapshot.RowCount);
                cmd.ExecuteNonQuery();
            }
        }

        private static int InsertSourceCells(SqlConnection connection, SqlTransaction tx, XltSnapshot snapshot)
        {
            var table = new DataTable();
            table.Columns.Add("FileName", typeof(string));
            table.Columns.Add("RowIndex", typeof(int));
            table.Columns.Add("ColumnIndex", typeof(int));
            table.Columns.Add("CellValue", typeof(string));

            for (var row = 0; row < snapshot.Rows.Count; row++)
            {
                for (var col = 0; col < snapshot.Rows[row].Length; col++)
                    table.Rows.Add(snapshot.FileName, row, col, (object)snapshot.Rows[row][col] ?? DBNull.Value);
            }

            using (var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, tx))
            {
                copy.DestinationTableName = "dbo.license_source_cells";
                copy.BulkCopyTimeout = 120;
                copy.WriteToServer(table);
            }
            return table.Rows.Count;
        }

        private static int InsertLicenseCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot snapshot)
        {
            var count = 0;
            foreach (var pair in EnumerateLicenseRows(snapshot))
            {
                var id = pair.Item1;
                var rowIndex = pair.Item2;
                var row = pair.Item3;
                var name = FindDisplayText(row, null);
                var category = FindCategory(row);
                var grade = FindGrade(row);

                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_catalog(LicenseId, SourceRow, Name, Category, Grade, RawData, UpdatedAt)
VALUES(@id, @row, @name, @category, @grade, @raw, SYSUTCDATETIME());", connection, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@row", rowIndex);
                    cmd.Parameters.AddWithValue("@name", (object)name ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@category", (object)category ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@grade", (object)grade ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@raw", JoinRaw(row));
                    cmd.ExecuteNonQuery();
                }
                count++;
            }
            return count;
        }

        private static int InsertRequirementCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot snapshot)
        {
            var count = 0;
            foreach (var pair in EnumerateLicenseRows(snapshot))
            {
                var licenseId = pair.Item1;
                var row = pair.Item3;
                var slot = 0;
                for (var col = 0; col < row.Length; col++)
                {
                    var key = row[col] ?? string.Empty;
                    if (!key.StartsWith("GC_", StringComparison.OrdinalIgnoreCase)) continue;

                    long value;
                    var hasValue = TryFindNumericRight(row, col, out value);
                    var param = FindParamRight(row, col, "GC_", "ME_");
                    var raw = JoinWindow(row, col, 5);

                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_requirements
(LicenseId, Slot, RequirementKey, RequirementValue, RequirementParam, SourceColumn, RawData)
VALUES(@license, @slot, @key, @value, @param, @column, @raw);", connection, tx))
                    {
                        cmd.Parameters.AddWithValue("@license", licenseId);
                        cmd.Parameters.AddWithValue("@slot", slot++);
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@value", hasValue ? (object)value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@param", (object)param ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@column", col);
                        cmd.Parameters.AddWithValue("@raw", raw);
                        cmd.ExecuteNonQuery();
                    }
                    count++;
                }
            }
            return count;
        }

        private static int InsertEffectCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot snapshot)
        {
            var count = 0;
            foreach (var pair in EnumerateLicenseRows(snapshot))
            {
                var licenseId = pair.Item1;
                var row = pair.Item3;
                var slot = 0;
                for (var col = 0; col < row.Length; col++)
                {
                    var key = row[col] ?? string.Empty;
                    if (!key.StartsWith("ME_", StringComparison.OrdinalIgnoreCase)) continue;

                    long value;
                    var hasValue = TryFindNumericRight(row, col, out value);
                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_effects
(LicenseId, Slot, EffectKey, EffectValue, SourceColumn, RawData)
VALUES(@license, @slot, @key, @value, @column, @raw);", connection, tx))
                    {
                        cmd.Parameters.AddWithValue("@license", licenseId);
                        cmd.Parameters.AddWithValue("@slot", slot++);
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@value", hasValue ? (object)value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@column", col);
                        cmd.Parameters.AddWithValue("@raw", JoinWindow(row, col, 4));
                        cmd.ExecuteNonQuery();
                    }
                    count++;
                }
            }
            return count;
        }

        private static void InsertKeyCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot snapshot,
            string prefix, string tableName, string keyColumn)
        {
            for (var rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
            {
                var row = snapshot.Rows[rowIndex];
                var key = row.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(key)) continue;

                var display = FindDisplayText(row, key);
                var sql = "INSERT INTO " + tableName + "(" + keyColumn + ", SourceRow, DisplayName, RawData) " +
                          "VALUES(@key, @row, @display, @raw);";
                using (var cmd = new SqlCommand(sql, connection, tx))
                {
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.Parameters.AddWithValue("@row", rowIndex);
                    cmd.Parameters.AddWithValue("@display", (object)display ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@raw", JoinRaw(row));
                    try { cmd.ExecuteNonQuery(); }
                    catch (SqlException ex)
                    {
                        // Some client tables contain aliases/duplicate keys. Preserve all
                        // raw cells, while the normalized dictionary keeps the first row.
                        if (ex.Number != 2627 && ex.Number != 2601) throw;
                    }
                }
            }
        }

        private static IEnumerable<Tuple<int, int, string[]>> EnumerateLicenseRows(XltSnapshot snapshot)
        {
            for (var rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
            {
                var row = snapshot.Rows[rowIndex];
                int id;
                if (!TryFindLicenseId(row, out id)) continue;
                yield return Tuple.Create(id, rowIndex, row);
            }
        }

        private static bool TryFindLicenseId(string[] row, out int id)
        {
            id = 0;
            for (var i = 0; i < row.Length; i++)
            {
                int candidate;
                if (int.TryParse(row[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out candidate) &&
                    candidate >= 7000 && candidate < 8000)
                {
                    id = candidate;
                    return true;
                }
            }
            return false;
        }

        private static string FindDisplayText(string[] row, string excluded)
        {
            foreach (var value in row)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var text = value.Trim();
                if (string.Equals(text, excluded, StringComparison.OrdinalIgnoreCase)) continue;
                if (text.StartsWith("GC_", StringComparison.OrdinalIgnoreCase) || text.StartsWith("ME_", StringComparison.OrdinalIgnoreCase)) continue;
                long number;
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) continue;
                if (text.Length == 1) continue;
                if (!text.Any(char.IsLetter)) continue;
                return text;
            }
            return null;
        }

        private static string FindCategory(string[] row)
        {
            foreach (var value in row)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var text = value.Trim();
                if (text.IndexOf("LADDER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("HUV", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("BATTLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("MISSION", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("UNDER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("DELIVERY", StringComparison.OrdinalIgnoreCase) >= 0)
                    return text;
            }
            return null;
        }

        private static string FindGrade(string[] row)
        {
            foreach (var value in row)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var text = value.Trim();
                if (text.Length == 1 && char.IsLetter(text[0])) return text.ToUpperInvariant();
            }
            return null;
        }

        private static bool TryFindNumericRight(string[] row, int start, out long value)
        {
            value = 0;
            for (var i = start + 1; i < row.Length && i <= start + 4; i++)
            {
                if (long.TryParse(row[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            }
            return false;
        }

        private static string FindParamRight(string[] row, int start, params string[] stopPrefixes)
        {
            for (var i = start + 1; i < row.Length && i <= start + 4; i++)
            {
                var value = row[i];
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (stopPrefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase))) break;
                long number;
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) continue;
                return value;
            }
            return null;
        }

        private static string JoinRaw(string[] row)
        {
            return string.Join("\u001F", row.Select(x => x ?? string.Empty));
        }

        private static string JoinWindow(string[] row, int start, int length)
        {
            return string.Join(" | ", row.Skip(start).Take(length).Select(x => x ?? string.Empty));
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
