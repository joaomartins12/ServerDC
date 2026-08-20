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
    /// Imports Drift City v0.77 license/title XLT files.
    ///
    /// These XLTs are UTF-16LE tab-separated text tables, not binary TDF containers.
    /// Fields may be quoted and may contain embedded CR/LF (License.xlt uses this for
    /// GainConditionText), so parsing must be record-aware rather than Split('\n').
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
            public int DeclaredCount;
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

            ValidateExpectedTables(snapshots);

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

            if (file.Length < 4)
                throw new InvalidDataException(fileName + " is too small to be a license XLT file.");

            if (file[0] != 0xFF || file[1] != 0xFE)
                throw new InvalidDataException(fileName + " is not UTF-16LE with BOM as expected by this client build.");

            string text;
            try
            {
                text = Encoding.Unicode.GetString(file, 2, file.Length - 2);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(fileName + " could not be decoded as UTF-16LE: " + ex.Message, ex);
            }

            var rows = ParseTabularText(text, fileName);
            if (rows.Count < 3)
                throw new InvalidDataException(fileName + " does not contain the Preset/Value/Index header records.");

            var columnCount = rows.Max(r => r.Length);
            if (columnCount <= 0 || columnCount > 512)
                throw new InvalidDataException(fileName + " contains invalid column count " + columnCount + ".");

            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Length == columnCount) continue;
                var expanded = new string[columnCount];
                Array.Copy(rows[i], expanded, rows[i].Length);
                for (var c = rows[i].Length; c < expanded.Length; c++) expanded[c] = string.Empty;
                rows[i] = expanded;
            }

            if (!CellEquals(rows[0], 0, "Preset") || !CellEquals(rows[1], 0, "Value") || !CellEquals(rows[2], 0, "Index"))
                throw new InvalidDataException(fileName + " has an unexpected XLT text header. Expected Preset / Value / Index.");

            int declaredCount;
            if (!int.TryParse(GetCell(rows[1], 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out declaredCount))
                throw new InvalidDataException(fileName + " has an invalid Value/Cnt declaration: '" + GetCell(rows[1], 1) + "'.");

            var snapshot = new XltSnapshot
            {
                FullPath = path,
                FileName = fileName,
                Hash = Sha256(file),
                HeaderBytes = 2,
                VersionMajor = 0,
                VersionMinor = 0,
                SourceYear = 0,
                SourceMonth = 0,
                SourceDay = 0,
                Flag = 0,
                HeaderOffset = 0,
                ColumnCount = columnCount,
                RowCount = rows.Count,
                DeclaredCount = declaredCount
            };
            snapshot.Rows.AddRange(rows);

            var validDataRows = CountIndexedDataRows(snapshot);
            if (validDataRows != declaredCount)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "{0} declares {1} data rows but {2} indexed rows were parsed. Records={3}, Columns={4}.",
                    fileName, declaredCount, validDataRows, rows.Count, columnCount));
            }

            return snapshot;
        }

        private static List<string[]> ParseTabularText(string text, string fileName)
        {
            var result = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(ch);
                    }
                    continue;
                }

                if (ch == '"' && field.Length == 0)
                {
                    inQuotes = true;
                    continue;
                }

                if (ch == '\t')
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                    continue;
                }

                if (ch == '\r' || ch == '\n')
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                    result.Add(fields.ToArray());
                    fields.Clear();

                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    continue;
                }

                field.Append(ch);
            }

            if (inQuotes)
                throw new InvalidDataException(fileName + " contains an unterminated quoted field.");

            if (field.Length != 0 || fields.Count != 0)
            {
                fields.Add(field.ToString());
                result.Add(fields.ToArray());
            }

            while (result.Count > 3 && result[result.Count - 1].All(string.IsNullOrEmpty))
                result.RemoveAt(result.Count - 1);

            return result;
        }

        private static void ValidateExpectedTables(Dictionary<string, XltSnapshot> tables)
        {
            XltSnapshot license;
            XltSnapshot gc;
            XltSnapshot me;
            if (!tables.TryGetValue("License.xlt", out license) ||
                !tables.TryGetValue("License_GC.xlt", out gc) ||
                !tables.TryGetValue("License_ME.xlt", out me))
                throw new InvalidDataException("The three required license tables were not parsed.");

            if (license.DeclaredCount != 72)
                throw new InvalidDataException("License.xlt expected 72 licenses for this client build, got " + license.DeclaredCount + ".");
            if (gc.DeclaredCount != 200)
                throw new InvalidDataException("License_GC.xlt expected 200 conditions for this client build, got " + gc.DeclaredCount + ".");
            if (me.DeclaredCount != 31)
                throw new InvalidDataException("License_ME.xlt expected 31 effects for this client build, got " + me.DeclaredCount + ".");

            if (!HeaderContains(license, "LicenseID") || !HeaderContains(license, "Condition_1") || !HeaderContains(license, "MountEffectID"))
                throw new InvalidDataException("License.xlt is missing required LicenseID/Condition/MountEffect columns.");
            if (!HeaderContains(gc, "GC_IDNAME"))
                throw new InvalidDataException("License_GC.xlt is missing GC_IDNAME.");
            if (!HeaderContains(me, "ME_IDNAME"))
                throw new InvalidDataException("License_ME.xlt is missing ME_IDNAME.");
        }

        private static bool HeaderContains(XltSnapshot snapshot, string name)
        {
            return snapshot.Rows.Count > 2 && snapshot.Rows[2].Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        }

        private static int CountIndexedDataRows(XltSnapshot snapshot)
        {
            var count = 0;
            for (var i = 3; i < snapshot.Rows.Count; i++)
            {
                int index;
                if (int.TryParse(GetCell(snapshot.Rows[i], 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                    count++;
            }
            return count;
        }

        private static bool CellEquals(string[] row, int index, string value)
        {
            return string.Equals(GetCell(row, index), value, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCell(string[] row, int index)
        {
            if (row == null || index < 0 || index >= row.Length) return string.Empty;
            return (row[index] ?? string.Empty).Trim();
        }

        private static int GetColumn(XltSnapshot snapshot, string name)
        {
            if (snapshot.Rows.Count <= 2) return -1;
            for (var i = 0; i < snapshot.Rows[2].Length; i++)
                if (string.Equals(GetCell(snapshot.Rows[2], i), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static void EnsureSchema(SqlConnection connection, SqlTransaction tx)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.license_source_files', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_source_files
    (
        [FileName] NVARCHAR(64) NOT NULL CONSTRAINT PK_license_source_files PRIMARY KEY,
        [FileHash] CHAR(64) NOT NULL,
        [HeaderBytes] INT NOT NULL,
        [VersionMajor] SMALLINT NOT NULL,
        [VersionMinor] SMALLINT NOT NULL,
        [SourceYear] SMALLINT NOT NULL,
        [SourceMonth] TINYINT NOT NULL,
        [SourceDay] TINYINT NOT NULL,
        [Flag] BIGINT NOT NULL,
        [HeaderOffset] BIGINT NOT NULL,
        [ColumnCount] INT NOT NULL,
        [RowCount] INT NOT NULL,
        [ImportedAt] DATETIME2 NOT NULL CONSTRAINT DF_license_source_files_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
END;
IF OBJECT_ID(N'dbo.license_source_cells', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_source_cells
    (
        [FileName] NVARCHAR(64) NOT NULL,
        [RowIndex] INT NOT NULL,
        [ColumnIndex] INT NOT NULL,
        [CellValue] NVARCHAR(MAX) NULL,
        CONSTRAINT PK_license_source_cells PRIMARY KEY ([FileName], [RowIndex], [ColumnIndex])
    );
END;
IF OBJECT_ID(N'dbo.license_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_catalog
    (
        [LicenseId] INT NOT NULL CONSTRAINT PK_license_catalog PRIMARY KEY,
        [SourceRow] INT NOT NULL,
        [Name] NVARCHAR(256) NULL,
        [Category] NVARCHAR(128) NULL,
        [Grade] NVARCHAR(32) NULL,
        [RawData] NVARCHAR(MAX) NOT NULL,
        [UpdatedAt] DATETIME2 NOT NULL CONSTRAINT DF_license_catalog_UpdatedAt DEFAULT(SYSUTCDATETIME())
    );
END;
IF OBJECT_ID(N'dbo.license_requirements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_requirements
    (
        [LicenseId] INT NOT NULL,
        [Slot] INT NOT NULL,
        [RequirementKey] NVARCHAR(128) NOT NULL,
        [RequirementValue] BIGINT NULL,
        [RequirementParam] NVARCHAR(512) NULL,
        [SourceColumn] INT NOT NULL,
        [RawData] NVARCHAR(1024) NULL,
        CONSTRAINT PK_license_requirements PRIMARY KEY ([LicenseId], [Slot])
    );
END;
IF OBJECT_ID(N'dbo.license_effects', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_effects
    (
        [LicenseId] INT NOT NULL,
        [Slot] INT NOT NULL,
        [EffectKey] NVARCHAR(128) NOT NULL,
        [EffectValue] BIGINT NULL,
        [SourceColumn] INT NOT NULL,
        [RawData] NVARCHAR(1024) NULL,
        CONSTRAINT PK_license_effects PRIMARY KEY ([LicenseId], [Slot])
    );
END;
IF OBJECT_ID(N'dbo.license_condition_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_condition_catalog
    (
        [ConditionKey] NVARCHAR(128) NOT NULL CONSTRAINT PK_license_condition_catalog PRIMARY KEY,
        [SourceRow] INT NOT NULL,
        [DisplayName] NVARCHAR(256) NULL,
        [RawData] NVARCHAR(MAX) NOT NULL
    );
END;
IF OBJECT_ID(N'dbo.license_effect_catalog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.license_effect_catalog
    (
        [EffectKey] NVARCHAR(128) NOT NULL CONSTRAINT PK_license_effect_catalog PRIMARY KEY,
        [SourceRow] INT NOT NULL,
        [DisplayName] NVARCHAR(256) NULL,
        [RawData] NVARCHAR(MAX) NOT NULL
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
([FileName], [FileHash], [HeaderBytes], [VersionMajor], [VersionMinor], [SourceYear], [SourceMonth], [SourceDay],
 [Flag], [HeaderOffset], [ColumnCount], [RowCount], [ImportedAt])
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
            var idColumn = GetColumn(s, "LicenseID");
            if (idColumn < 0) yield break;

            for (var rowIndex = 3; rowIndex < s.Rows.Count; rowIndex++)
            {
                var row = s.Rows[rowIndex];
                int id;
                if (!int.TryParse(GetCell(row, idColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
                if (id < 7000 || id >= 8000) continue;
                yield return Tuple.Create(id, rowIndex, row);
            }
        }

        private static int InsertLicenseCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            var nameColumn = GetColumn(s, "LicenseName");
            var categoryColumn = GetColumn(s, "CategoryID");
            var gradeColumn = GetColumn(s, "Grade");
            var count = 0;

            foreach (var pair in EnumerateLicenseRows(s))
            {
                var id = pair.Item1;
                var rowIndex = pair.Item2;
                var row = pair.Item3;
                var name = nameColumn >= 0 ? GetCell(row, nameColumn) : null;
                var category = categoryColumn >= 0 ? GetCell(row, categoryColumn) : null;
                var grade = gradeColumn >= 0 ? GetCell(row, gradeColumn) : null;

                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_catalog(LicenseId,SourceRow,Name,Category,Grade,RawData,UpdatedAt)
VALUES(@id,@row,@name,@category,@grade,@raw,SYSUTCDATETIME());", connection, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@row", rowIndex);
                    cmd.Parameters.AddWithValue("@name", string.IsNullOrEmpty(name) ? (object)DBNull.Value : name);
                    cmd.Parameters.AddWithValue("@category", string.IsNullOrEmpty(category) ? (object)DBNull.Value : category);
                    cmd.Parameters.AddWithValue("@grade", string.IsNullOrEmpty(grade) ? (object)DBNull.Value : grade);
                    cmd.Parameters.AddWithValue("@raw", JoinRaw(row));
                    cmd.ExecuteNonQuery();
                }
                count++;
            }
            return count;
        }

        private static int InsertRequirementCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            var conditionColumns = new List<int>();
            for (var i = 1; i <= 12; i++)
            {
                var column = GetColumn(s, "Condition_" + i.ToString(CultureInfo.InvariantCulture));
                if (column >= 0) conditionColumns.Add(column);
            }

            var count = 0;
            foreach (var pair in EnumerateLicenseRows(s))
            {
                var licenseId = pair.Item1;
                var row = pair.Item3;
                var slot = 0;

                foreach (var col in conditionColumns)
                {
                    var raw = GetCell(row, col);
                    string key;
                    long? value;
                    string param;
                    if (!TryParseRequirement(raw, out key, out value, out param)) continue;

                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_requirements
(LicenseId,Slot,RequirementKey,RequirementValue,RequirementParam,SourceColumn,RawData)
VALUES(@id,@slot,@key,@value,@param,@column,@raw);", connection, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", licenseId);
                        cmd.Parameters.AddWithValue("@slot", slot++);
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@value", value.HasValue ? (object)value.Value : DBNull.Value);
                        cmd.Parameters.AddWithValue("@param", string.IsNullOrEmpty(param) ? (object)DBNull.Value : param);
                        cmd.Parameters.AddWithValue("@column", col);
                        cmd.Parameters.AddWithValue("@raw", raw.Length > 1024 ? raw.Substring(0, 1024) : raw);
                        cmd.ExecuteNonQuery();
                    }
                    count++;
                }
            }
            return count;
        }

        private static bool TryParseRequirement(string raw, out string key, out long? value, out string param)
        {
            key = null;
            value = null;
            param = null;

            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0 || raw.Equals("n/a", StringComparison.OrdinalIgnoreCase)) return false;

            var parts = raw.Split(new[] { ':' }, 3);
            key = parts[0].Trim();
            if (!key.StartsWith("GC_", StringComparison.OrdinalIgnoreCase)) return false;

            if (parts.Length >= 2)
            {
                long parsed;
                if (long.TryParse(parts[1].Trim().Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    value = parsed;
                else if (parts[1].Trim().Length != 0)
                    param = parts[1].Trim();
            }
            if (parts.Length >= 3 && parts[2].Trim().Length != 0)
                param = string.IsNullOrEmpty(param) ? parts[2].Trim() : param + ":" + parts[2].Trim();

            return true;
        }

        private static int InsertEffectCatalog(SqlConnection connection, SqlTransaction tx, XltSnapshot s)
        {
            var effectIdColumns = new[] { GetColumn(s, "MountEffectID"), GetColumn(s, "MountEffectID_second") };
            var effectValueColumns = new[] { GetColumn(s, "MountEffectValue"), GetColumn(s, "MountEffectValue_second") };
            var count = 0;

            foreach (var pair in EnumerateLicenseRows(s))
            {
                var licenseId = pair.Item1;
                var row = pair.Item3;
                var slot = 0;

                for (var i = 0; i < effectIdColumns.Length; i++)
                {
                    var idColumn = effectIdColumns[i];
                    if (idColumn < 0) continue;
                    var key = GetCell(row, idColumn);
                    if (!key.StartsWith("ME_", StringComparison.OrdinalIgnoreCase)) continue;

                    long value;
                    object dbValue = DBNull.Value;
                    var valueColumn = effectValueColumns[i];
                    if (valueColumn >= 0 && long.TryParse(GetCell(row, valueColumn), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                        dbValue = value;

                    using (var cmd = new SqlCommand(@"
INSERT INTO dbo.license_effects
(LicenseId,Slot,EffectKey,EffectValue,SourceColumn,RawData)
VALUES(@id,@slot,@key,@value,@column,@raw);", connection, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", licenseId);
                        cmd.Parameters.AddWithValue("@slot", slot++);
                        cmd.Parameters.AddWithValue("@key", key);
                        cmd.Parameters.AddWithValue("@value", dbValue);
                        cmd.Parameters.AddWithValue("@column", idColumn);
                        cmd.Parameters.AddWithValue("@raw", key + ":" + (valueColumn >= 0 ? GetCell(row, valueColumn) : string.Empty));
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
            var headerName = prefix.Equals("GC_", StringComparison.OrdinalIgnoreCase) ? "GC_IDNAME" : "ME_IDNAME";
            var keyIndex = GetColumn(s, headerName);
            if (keyIndex < 0) return;

            var descriptionIndex = GetColumn(s,
                prefix.Equals("GC_", StringComparison.OrdinalIgnoreCase) ? "GC_Desc" : "ME_Desc");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var rowIndex = 3; rowIndex < s.Rows.Count; rowIndex++)
            {
                int sourceIndex;
                if (!int.TryParse(GetCell(s.Rows[rowIndex], 0), NumberStyles.Integer, CultureInfo.InvariantCulture, out sourceIndex)) continue;

                var row = s.Rows[rowIndex];
                var key = GetCell(row, keyIndex);
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(key)) continue;
                var display = descriptionIndex >= 0 ? GetCell(row, descriptionIndex) : null;

                var sql = "INSERT INTO " + table + "(" + keyColumn + ",SourceRow,DisplayName,RawData) VALUES(@key,@row,@display,@raw);";
                using (var cmd = new SqlCommand(sql, connection, tx))
                {
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.Parameters.AddWithValue("@row", rowIndex);
                    cmd.Parameters.AddWithValue("@display", string.IsNullOrEmpty(display) ? (object)DBNull.Value : display);
                    cmd.Parameters.AddWithValue("@raw", JoinRaw(row));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static string JoinRaw(string[] row)
        {
            return string.Join(" | ", row.Select(x => x ?? string.Empty).ToArray());
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
