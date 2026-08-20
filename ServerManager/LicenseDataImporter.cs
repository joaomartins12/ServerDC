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
    /// Imports the Drift City license/title XLT tables.
    ///
    /// XLT files shipped with different client builds can contain an outer wrapper before
    /// the actual table header. Do not assume that UInt16(file[2]) is always the table
    /// offset. Instead, detect the embedded table by validating dimensions, offset table
    /// bounds and UTF-16 string references.
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
            public bool AbsoluteStringOffsets;
            public readonly List<string[]> Rows = new List<string[]>();
        }

        private sealed class HeaderCandidate
        {
            public int BaseOffset;
            public int Columns;
            public int Rows;
            public bool AbsoluteStrings;
            public int Score;
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
            return RequiredFileNames.Where(name => !File.Exists(Path.Combine(folder, name))).ToArray();
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

                        XltSnapshot license;
                        if (snapshots.TryGetValue("License.xlt", out license))
                        {
                            licenses = InsertLicenseCatalog(connection, tx, license);
                            requirements = InsertRequirementCatalog(connection, tx, license);
                            effects = InsertEffectCatalog(connection, tx, license);
                        }

                        XltSnapshot gc;
                        if (snapshots.TryGetValue("License_GC.xlt", out gc))
                            InsertKeyCatalog(connection, tx, gc, "GC_", "dbo.license_condition_catalog", "ConditionKey");

                        XltSnapshot me;
                        if (snapshots.TryGetValue("License_ME.xlt", out me))
                            InsertKeyCatalog(connection, tx, me, "ME_", "dbo.license_effect_catalog", "EffectKey");

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
            var fileName = Path.GetFileName(path);
            if (file.Length < 24)
                throw new InvalidDataException(fileName + " is too small to be an XLT table.");

            var candidates = FindHeaderCandidates(file, fileName);
            if (candidates.Count == 0)
            {
                var legacy = file.Length >= 4 ? BitConverter.ToUInt16(file, 2) : 0;
                throw new InvalidDataException(
                    fileName + " contains no valid XLT table header. FileSize=" + file.Length +
                    ", legacyHeaderHint=" + legacy + ". Please send this XLT file if this persists.");
            }

            var best = candidates.OrderByDescending(x => x.Score).First();
            var baseOffset = best.BaseOffset;

            var snapshot = new XltSnapshot
            {
                FullPath = path,
                FileName = fileName,
                Hash = Sha256(file),
                HeaderBytes = baseOffset,
                VersionMajor = BitConverter.ToInt16(file, baseOffset + 0),
                VersionMinor = BitConverter.ToInt16(file, baseOffset + 2),
                SourceYear = BitConverter.ToInt16(file, baseOffset + 4),
                SourceMonth = file[baseOffset + 6],
                SourceDay = file[baseOffset + 7],
                Flag = BitConverter.ToUInt32(file, baseOffset + 8),
                HeaderOffset = BitConverter.ToUInt32(file, baseOffset + 12),
                ColumnCount = best.Columns,
                RowCount = best.Rows,
                AbsoluteStringOffsets = best.AbsoluteStrings
            };

            var cursor = baseOffset + 24;
            for (var row = 0; row < snapshot.RowCount; row++)
            {
                var values = new string[snapshot.ColumnCount];
                for (var column = 0; column < snapshot.ColumnCount; column++)
                {
                    var rawOffset = BitConverter.ToUInt32(file, cursor);
                    cursor += 4;
                    var stringOffset = ResolveStringOffset(rawOffset, baseOffset, snapshot.AbsoluteStringOffsets);
                    values[column] = ReadUnicodeString(file, stringOffset, snapshot.FileName, row, column);
                }
                snapshot.Rows.Add(values);
            }

            return snapshot;
        }

        private static List<HeaderCandidate> FindHeaderCandidates(byte[] file, string fileName)
        {
            var result = new List<HeaderCandidate>();
            var scanLimit = Math.Min(file.Length - 24, 16384);

            // First try the hint used by some TDF/XLT builds, then scan the wrapper.
            var preferred = new List<int>();
            if (file.Length >= 4)
            {
                var hint16 = (int)BitConverter.ToUInt16(file, 2);
                if (hint16 >= 0 && hint16 <= scanLimit) preferred.Add(hint16);
            }
            preferred.Add(0);

            foreach (var offset in preferred.Distinct())
                TryAddCandidate(file, offset, result);

            for (var offset = 1; offset <= scanLimit; offset++)
            {
                if (preferred.Contains(offset)) continue;
                TryAddCandidate(file, offset, result);
            }

            // License tables should contain recognisable key text. Give such candidates
            // a large bonus so a coincidental integer pattern in the wrapper cannot win.
            foreach (var candidate in result)
            {
                var sampleText = ReadCandidateSample(file, candidate, 96);
                if (fileName.Equals("License.xlt", StringComparison.OrdinalIgnoreCase))
                {
                    if (sampleText.IndexOf("7000", StringComparison.OrdinalIgnoreCase) >= 0) candidate.Score += 100;
                    if (sampleText.IndexOf("GC_", StringComparison.OrdinalIgnoreCase) >= 0) candidate.Score += 80;
                    if (sampleText.IndexOf("ME_", StringComparison.OrdinalIgnoreCase) >= 0) candidate.Score += 60;
                }
                else if (fileName.Equals("License_GC.xlt", StringComparison.OrdinalIgnoreCase))
                {
                    if (sampleText.IndexOf("GC_", StringComparison.OrdinalIgnoreCase) >= 0) candidate.Score += 160;
                }
                else if (fileName.Equals("License_ME.xlt", StringComparison.OrdinalIgnoreCase))
                {
                    if (sampleText.IndexOf("ME_", StringComparison.OrdinalIgnoreCase) >= 0) candidate.Score += 160;
                }
            }

            return result;
        }

        private static void TryAddCandidate(byte[] file, int baseOffset, List<HeaderCandidate> result)
        {
            if (baseOffset < 0 || baseOffset + 24 > file.Length) return;

            uint columnsRaw;
            uint rowsRaw;
            try
            {
                columnsRaw = BitConverter.ToUInt32(file, baseOffset + 16);
                rowsRaw = BitConverter.ToUInt32(file, baseOffset + 20);
            }
            catch
            {
                return;
            }

            if (columnsRaw == 0 || columnsRaw > 1024 || rowsRaw == 0 || rowsRaw > 200000)
                return;

            var cells = (long)columnsRaw * rowsRaw;
            if (cells <= 0 || cells > 4000000) return;

            var offsetTableEnd = baseOffset + 24L + cells * 4L;
            if (offsetTableEnd > file.Length) return;

            var relativeScore = ScoreOffsets(file, baseOffset, (int)columnsRaw, (int)rowsRaw, false);
            var absoluteScore = ScoreOffsets(file, baseOffset, (int)columnsRaw, (int)rowsRaw, true);
            var bestScore = Math.Max(relativeScore, absoluteScore);
            if (bestScore < 10) return;

            var score = bestScore;
            var year = BitConverter.ToInt16(file, baseOffset + 4);
            var month = file[baseOffset + 6];
            var day = file[baseOffset + 7];
            if (year >= 2000 && year <= 2100) score += 8;
            if (month >= 1 && month <= 12) score += 3;
            if (day >= 1 && day <= 31) score += 2;
            if (rowsRaw >= 20 && rowsRaw <= 1000) score += 5;
            if (columnsRaw >= 2 && columnsRaw <= 256) score += 5;

            result.Add(new HeaderCandidate
            {
                BaseOffset = baseOffset,
                Columns = (int)columnsRaw,
                Rows = (int)rowsRaw,
                AbsoluteStrings = absoluteScore > relativeScore,
                Score = score
            });
        }

        private static int ScoreOffsets(byte[] file, int baseOffset, int columns, int rows, bool absolute)
        {
            var cells = (long)columns * rows;
            var sampleCount = (int)Math.Min(cells, 48L);
            if (sampleCount <= 0) return 0;

            var tableStart = baseOffset + 24;
            var valid = 0;
            var textQuality = 0;

            for (var i = 0; i < sampleCount; i++)
            {
                var cursor = tableStart + i * 4;
                if (cursor + 4 > file.Length) break;

                var raw = BitConverter.ToUInt32(file, cursor);
                var resolved = ResolveStringOffset(raw, baseOffset, absolute);
                string text;
                if (!TryReadUnicodeString(file, resolved, out text)) continue;

                valid++;
                if (text.Length == 0) textQuality += 1;
                else if (IsReasonableText(text)) textQuality += 3;
                else textQuality -= 2;
            }

            if (valid < Math.Max(3, sampleCount / 3)) return 0;
            return valid * 2 + textQuality;
        }

        private static string ReadCandidateSample(byte[] file, HeaderCandidate candidate, int maxCells)
        {
            var sb = new StringBuilder();
            var total = Math.Min((long)candidate.Columns * candidate.Rows, maxCells);
            var cursor = candidate.BaseOffset + 24;

            for (var i = 0L; i < total; i++)
            {
                if (cursor + 4 > file.Length) break;
                var raw = BitConverter.ToUInt32(file, cursor);
                cursor += 4;
                var resolved = ResolveStringOffset(raw, candidate.BaseOffset, candidate.AbsoluteStrings);
                string text;
                if (!TryReadUnicodeString(file, resolved, out text)) continue;
                if (text.Length == 0) continue;
                sb.Append(text).Append('|');
            }
            return sb.ToString();
        }

        private static int ResolveStringOffset(uint rawOffset, int baseOffset, bool absolute)
        {
            if (rawOffset > int.MaxValue) return -1;
            if (absolute) return (int)rawOffset;
            var resolved = (long)baseOffset + rawOffset;
            return resolved > int.MaxValue ? -1 : (int)resolved;
        }

        private static bool TryReadUnicodeString(byte[] file, int offset, out string text)
        {
            text = string.Empty;
            if (offset < 0 || offset + 1 >= file.Length) return false;

            var end = offset;
            var maxEnd = Math.Min(file.Length - 1, offset + 8192);
            while (end + 1 <= maxEnd)
            {
                if (file[end] == 0 && file[end + 1] == 0)
                {
                    if (((end - offset) & 1) != 0) return false;
                    try
                    {
                        text = Encoding.Unicode.GetString(file, offset, end - offset);
                        return IsReasonableText(text);
                    }
                    catch
                    {
                        return false;
                    }
                }
                end += 2;
            }
            return false;
        }

        private static string ReadUnicodeString(byte[] file, int offset, string fileName, int row, int column)
        {
            string text;
            if (!TryReadUnicodeString(file, offset, out text))
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "{0}: invalid UTF-16 string offset {1} at row {2}, column {3}.",
                    fileName, offset, row, column));
            }
            return text;
        }

        private static bool IsReasonableText(string text)
        {
            if (text == null) return false;
            if (text.Length == 0) return true;
            if (text.Length > 4096) return false;

            var bad = 0;
            foreach (var ch in text)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t') continue;
                if (char.IsControl(ch)) bad++;
            }
            return bad <= Math.Max(1, text.Length / 20);
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
            using (var cmd = new SqlCommand(sql, connection, tx)) cmd.ExecuteNonQuery();
        }

        private static void InsertSourceFile(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            const string sql = @"
INSERT INTO dbo.license_source_files
(FileName, FileHash, HeaderBytes, VersionMajor, VersionMinor, SourceYear, SourceMonth, SourceDay,
 Flag, HeaderOffset, ColumnCount, RowCount, ImportedAt)
VALUES(@file,@hash,@header,@major,@minor,@year,@month,@day,@flag,@offset,@columns,@rows,SYSUTCDATETIME());";
            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@file", s.FileName);
                cmd.Parameters.AddWithValue("@hash", s.Hash);
                cmd.Parameters.AddWithValue("@header", s.HeaderBytes);
                cmd.Parameters.AddWithValue("@major", s.VersionMajor);
                cmd.Parameters.AddWithValue("@minor", s.VersionMinor);
                cmd.Parameters.AddWithValue("@year", s.SourceYear);
                cmd.Parameters.AddWithValue("@month", s.SourceMonth);
                cmd.Parameters.AddWithValue("@day", s.SourceDay);
                cmd.Parameters.AddWithValue("@flag", (long)s.Flag);
                cmd.Parameters.AddWithValue("@offset", (long)s.HeaderOffset);
                cmd.Parameters.AddWithValue("@columns", s.ColumnCount);
                cmd.Parameters.AddWithValue("@rows", s.RowCount);
                cmd.ExecuteNonQuery();
            }
        }

        private static int InsertSourceCells(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            var table = new DataTable();
            table.Columns.Add("FileName", typeof(string));
            table.Columns.Add("RowIndex", typeof(int));
            table.Columns.Add("ColumnIndex", typeof(int));
            table.Columns.Add("CellValue", typeof(string));

            for (var r = 0; r < s.Rows.Count; r++)
                for (var c = 0; c < s.Rows[r].Length; c++)
                    table.Rows.Add(s.FileName, r, c, (object)s.Rows[r][c] ?? DBNull.Value);

            using (var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, tx))
            {
                copy.DestinationTableName = "dbo.license_source_cells";
                copy.BulkCopyTimeout = 120;
                copy.WriteToServer(table);
            }
            return table.Rows.Count;
        }

        private static IEnumerable<Tuple<int, int, string[]>> EnumerateLicenseRows(XltSnapshot s)
        {
            for (var rowIndex = 0; rowIndex < s.Rows.Count; rowIndex++)
            {
                var row = s.Rows[rowIndex];
                int id;
                if (!TryFindLicenseId(row, out id)) continue;
                yield return Tuple.Create(id, rowIndex, row);
            }
        }

        private static bool TryFindLicenseId(string[] row, out int id)
        {
            id = 0;
            foreach (var value in row)
            {
                int parsed;
                if (!int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    continue;
                if (parsed >= 7000 && parsed < 8000)
                {
                    id = parsed;
                    return true;
                }
            }
            return false;
        }

        private static int InsertLicenseCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            var count = 0;
            foreach (var pair in EnumerateLicenseRows(s))
            {
                var id = pair.Item1;
                var rowIndex = pair.Item2;
                var row = pair.Item3;
                var name = FindDisplayText(row);
                var category = FindCategory(row);
                var grade = FindGrade(row);

                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_catalog(LicenseId,SourceRow,Name,Category,Grade,RawData,UpdatedAt)
VALUES(@id,@row,@name,@category,@grade,@raw,SYSUTCDATETIME());", connection, tx))
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

        private static int InsertRequirementCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            var count = 0;
            foreach (var pair in EnumerateLicenseRows(s))
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
                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_requirements
(LicenseId,Slot,RequirementKey,RequirementValue,RequirementParam,SourceColumn,RawData)
VALUES(@id,@slot,@key,@value,@param,@column,@raw);", connection, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", licenseId);
                        cmd.Parameters.AddWithValue("@slot", slot++);
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@value", hasValue ? (object)value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@param", (object)param ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@column", col);
                        cmd.Parameters.AddWithValue("@raw", JoinWindow(row, col, 5));
                        cmd.ExecuteNonQuery();
                    }
                    count++;
                }
            }
            return count;
        }

        private static int InsertEffectCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            var count = 0;
            foreach (var pair in EnumerateLicenseRows(s))
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
(LicenseId,Slot,EffectKey,EffectValue,SourceColumn,RawData)
VALUES(@id,@slot,@key,@value,@column,@raw);", connection, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", licenseId);
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

        private static void InsertKeyCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot s,
            string prefix, string table, string keyColumn)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var rowIndex = 0; rowIndex < s.Rows.Count; rowIndex++)
            {
                var row = s.Rows[rowIndex];
                for (var col = 0; col < row.Length; col++)
                {
                    var key = row[col] ?? string.Empty;
                    if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(key)) continue;
                    var display = FindDisplayText(row);
                    var sql = "INSERT INTO " + table + "(" + keyColumn + ",SourceRow,DisplayName,RawData) VALUES(@key,@row,@display,@raw);";
                    using (var cmd = new SqlCommand(sql, connection, tx))
                    {
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@row", rowIndex);
                        cmd.Parameters.AddWithValue("@display", (object)display ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@raw", JoinRaw(row));
                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static bool TryFindNumericRight(string[] row, int start, out long value)
        {
            value = 0;
            for (var i = start + 1; i < row.Length && i <= start + 4; i++)
            {
                var text = (row[i] ?? string.Empty).Trim().Replace(",", string.Empty);
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            }
            return false;
        }

        private static string FindParamRight(string[] row, int start, params string[] ignoredPrefixes)
        {
            for (var i = start + 1; i < row.Length && i <= start + 4; i++)
            {
                var value = (row[i] ?? string.Empty).Trim();
                if (value.Length == 0) continue;
                long numeric;
                if (long.TryParse(value.Replace(",", string.Empty), out numeric)) continue;
                var ignored = ignoredPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (!ignored) return value;
            }
            return null;
        }

        private static string FindDisplayText(string[] row)
        {
            foreach (var raw in row)
            {
                var value = (raw ?? string.Empty).Trim();
                if (value.Length < 2 || value.Length > 256) continue;
                int numeric;
                if (int.TryParse(value, out numeric)) continue;
                if (value.StartsWith("GC_", StringComparison.OrdinalIgnoreCase)) continue;
                if (value.StartsWith("ME_", StringComparison.OrdinalIgnoreCase)) continue;
                return value;
            }
            return null;
        }

        private static string FindCategory(string[] row)
        {
            var known = new[] { "ROOKIE", "LADDER", "HUV", "BATTLE", "PATROL", "UNDERCITY", "MISSION", "QUEST", "CRASH", "DRIVER" };
            foreach (var raw in row)
            {
                var value = (raw ?? string.Empty).Trim();
                foreach (var token in known)
                    if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return token;
            }
            return null;
        }

        private static string FindGrade(string[] row)
        {
            foreach (var raw in row)
            {
                var value = (raw ?? string.Empty).Trim().ToUpperInvariant();
                if (value == "R" || value == "E" || value == "D" || value == "C" || value == "B" || value == "A" || value == "S")
                    return value;
            }
            return null;
        }

        private static string JoinRaw(string[] row)
        {
            return string.Join(" | ", row.Select(x => x ?? string.Empty).ToArray());
        }

        private static string JoinWindow(string[] row, int center, int radius)
        {
            var from = Math.Max(0, center - 1);
            var to = Math.Min(row.Length - 1, center + radius);
            var values = new List<string>();
            for (var i = from; i <= to; i++) values.Add(row[i] ?? string.Empty);
            var text = string.Join(" | ", values.ToArray());
            return text.Length > 1024 ? text.Substring(0, 1024) : text;
        }

        private static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
