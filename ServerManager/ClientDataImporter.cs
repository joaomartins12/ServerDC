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
    internal static class ClientDataImporter
    {
        internal sealed class ImportResult
        {
            public int Files;
            public long Rows;
            public int ItemLookupRows;
            public string Folder;
        }

        private sealed class TdfSnapshot
        {
            public string FileName;
            public string FullPath;
            public string TableName;
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
            public int? GlobalBaseIndex;
            public List<string[]> Rows = new List<string[]>();
        }

        public static string ImportFolder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Improter"); }
        }

        public static string EnsureImportDirectory()
        {
            Directory.CreateDirectory(ImportFolder);
            return ImportFolder;
        }

        public static int CountTdfFiles()
        {
            EnsureImportDirectory();
            return Directory.GetFiles(ImportFolder, "*.tdf", SearchOption.AllDirectories).Length;
        }

        public static ImportResult ImportAll()
        {
            var folder = EnsureImportDirectory();
            var paths = Directory.GetFiles(folder, "*.tdf", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
                throw new InvalidOperationException("No .tdf files were found in " + folder + ".");

            var snapshots = new List<TdfSnapshot>();
            var usedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var snapshot = Parse(path, folder);
                snapshot.TableName = BuildUniqueTableName(snapshot.FileName, usedTableNames);
                snapshots.Add(snapshot);
            }

            var itemClient = snapshots.FirstOrDefault(x =>
                string.Equals(Path.GetFileName(x.FileName), "ItemClient.tdf", StringComparison.OrdinalIgnoreCase));
            var useItemClient = snapshots.FirstOrDefault(x =>
                string.Equals(Path.GetFileName(x.FileName), "UseItemClient.tdf", StringComparison.OrdinalIgnoreCase));

            if (itemClient != null) itemClient.GlobalBaseIndex = 0;
            if (useItemClient != null && itemClient != null) useItemClient.GlobalBaseIndex = itemClient.RowCount;

            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = "DCServer",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity Global Client Data Importer"
            }.ConnectionString;

            long totalRows = 0;
            var lookupCount = 0;

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                using (var tx = connection.BeginTransaction())
                {
                    try
                    {
                        DropPreviousImportedTables(connection, tx);
                        DropLegacyGenericTables(connection, tx);
                        EnsureCoreSchema(connection, tx);

                        using (var clearLookup = new SqlCommand("DELETE FROM dbo.client_item_lookup;", connection, tx))
                            clearLookup.ExecuteNonQuery();
                        using (var clearManifest = new SqlCommand("DELETE FROM dbo.client_tdf_manifest;", connection, tx))
                            clearManifest.ExecuteNonQuery();

                        foreach (var snapshot in snapshots)
                        {
                            CreateSnapshotTable(connection, tx, snapshot);
                            BulkInsertSnapshot(connection, tx, snapshot);
                            InsertManifest(connection, tx, snapshot);
                            lookupCount += BulkInsertLookup(connection, tx, snapshot);
                            totalRows += snapshot.RowCount;
                        }

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
                Rows = totalRows,
                ItemLookupRows = lookupCount,
                Folder = folder
            };
        }

        private static TdfSnapshot Parse(string path, string root)
        {
            var file = File.ReadAllBytes(path);
            if (file.Length < 4)
                throw new InvalidDataException(Path.GetFileName(path) + " is too small to be a TDF file.");

            var headerBytes = BitConverter.ToUInt16(file, 2);
            if (headerBytes >= file.Length || file.Length - headerBytes < 24)
                throw new InvalidDataException(Path.GetFileName(path) + " has an invalid TDF header offset " + headerBytes + ".");

            var dataLength = file.Length - headerBytes;
            var data = new byte[dataLength];
            Buffer.BlockCopy(file, headerBytes, data, 0, dataLength);

            var snapshot = new TdfSnapshot();
            snapshot.FullPath = path;
            snapshot.FileName = MakeRelativePath(root, path);
            snapshot.Hash = Sha256(file);
            snapshot.HeaderBytes = headerBytes;
            snapshot.VersionMajor = BitConverter.ToInt16(data, 0);
            snapshot.VersionMinor = BitConverter.ToInt16(data, 2);
            snapshot.SourceYear = BitConverter.ToInt16(data, 4);
            snapshot.SourceMonth = data[6];
            snapshot.SourceDay = data[7];
            snapshot.Flag = BitConverter.ToUInt32(data, 8);
            snapshot.HeaderOffset = BitConverter.ToUInt32(data, 12);
            snapshot.ColumnCount = checked((int)BitConverter.ToUInt32(data, 16));
            snapshot.RowCount = checked((int)BitConverter.ToUInt32(data, 20));

            var offsetTableBytes = checked((long)snapshot.ColumnCount * snapshot.RowCount * 4L);
            if (24L + offsetTableBytes > data.Length)
                throw new InvalidDataException(snapshot.FileName + " has a truncated offset table.");

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
                    "{0} contains an invalid string offset {1} at row {2}, column {3}.",
                    fileName, offset, row, column));

            var start = checked((int)offset);
            var end = start;
            while (end + 1 < data.Length)
            {
                if (data[end] == 0 && data[end + 1] == 0) break;
                end += 2;
            }

            if (end + 1 >= data.Length)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "{0} contains an unterminated string at row {1}, column {2}.", fileName, row, column));

            return Encoding.Unicode.GetString(data, start, end - start);
        }

        private static void EnsureCoreSchema(SqlConnection connection, SqlTransaction tx)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.client_tdf_manifest', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.client_tdf_manifest
    (
        FileName NVARCHAR(260) NOT NULL CONSTRAINT PK_client_tdf_manifest PRIMARY KEY,
        TableName SYSNAME NOT NULL,
        FileHash CHAR(64) NOT NULL,
        HeaderBytes INT NOT NULL,
        VersionMajor SMALLINT NOT NULL,
        VersionMinor SMALLINT NOT NULL,
        SourceYear SMALLINT NOT NULL,
        SourceMonth TINYINT NOT NULL,
        SourceDay TINYINT NOT NULL,
        Flag BIGINT NOT NULL,
        HeaderOffset BIGINT NOT NULL,
        [ColumnCount] INT NOT NULL,
        [RowCount] INT NOT NULL,
        GlobalBaseIndex INT NULL,
        ImportedAt DATETIME2 NOT NULL CONSTRAINT DF_client_tdf_manifest_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
END
ELSE IF COL_LENGTH(N'dbo.client_tdf_manifest', N'TableName') IS NULL
BEGIN
    DROP TABLE dbo.client_tdf_manifest;
    CREATE TABLE dbo.client_tdf_manifest
    (
        FileName NVARCHAR(260) NOT NULL CONSTRAINT PK_client_tdf_manifest PRIMARY KEY,
        TableName SYSNAME NOT NULL,
        FileHash CHAR(64) NOT NULL,
        HeaderBytes INT NOT NULL,
        VersionMajor SMALLINT NOT NULL,
        VersionMinor SMALLINT NOT NULL,
        SourceYear SMALLINT NOT NULL,
        SourceMonth TINYINT NOT NULL,
        SourceDay TINYINT NOT NULL,
        Flag BIGINT NOT NULL,
        HeaderOffset BIGINT NOT NULL,
        [ColumnCount] INT NOT NULL,
        [RowCount] INT NOT NULL,
        GlobalBaseIndex INT NULL,
        ImportedAt DATETIME2 NOT NULL CONSTRAINT DF_client_tdf_manifest_ImportedAt DEFAULT(SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.client_item_lookup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.client_item_lookup
    (
        ClientTableIndex INT NOT NULL CONSTRAINT PK_client_item_lookup PRIMARY KEY,
        SourceFile NVARCHAR(260) NOT NULL,
        SourceTable SYSNAME NOT NULL,
        RowIndex INT NOT NULL,
        ItemId NVARCHAR(128) NULL,
        Category NVARCHAR(128) NULL,
        Name NVARCHAR(512) NULL
    );
    CREATE INDEX IX_client_item_lookup_ItemId ON dbo.client_item_lookup(ItemId);
    CREATE INDEX IX_client_item_lookup_Name ON dbo.client_item_lookup(Name);
END
ELSE IF COL_LENGTH(N'dbo.client_item_lookup', N'SourceTable') IS NULL
BEGIN
    ALTER TABLE dbo.client_item_lookup ADD SourceTable SYSNAME NULL;
END;";

            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.CommandTimeout = 120;
                cmd.ExecuteNonQuery();
            }
        }

        private static void DropPreviousImportedTables(SqlConnection connection, SqlTransaction tx)
        {
            if (!TableHasColumn(connection, tx, "dbo.client_tdf_manifest", "TableName"))
                return;

            var tableNames = new List<string>();
            using (var cmd = new SqlCommand("SELECT TableName FROM dbo.client_tdf_manifest WHERE TableName IS NOT NULL;", connection, tx))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    tableNames.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture));
            }

            foreach (var tableName in tableNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(tableName)) continue;
                using (var cmd = new SqlCommand(
                    "IF OBJECT_ID(N'dbo." + EscapeSqlLiteral(tableName) + "', N'U') IS NOT NULL DROP TABLE dbo." + QuoteIdentifier(tableName) + ";",
                    connection, tx))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static void DropLegacyGenericTables(SqlConnection connection, SqlTransaction tx)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.client_tdf_cells', N'U') IS NOT NULL DROP TABLE dbo.client_tdf_cells;
IF OBJECT_ID(N'dbo.client_tdf_rows', N'U') IS NOT NULL DROP TABLE dbo.client_tdf_rows;";
            using (var cmd = new SqlCommand(sql, connection, tx))
                cmd.ExecuteNonQuery();
        }

        private static bool TableHasColumn(SqlConnection connection, SqlTransaction tx, string tableName, string columnName)
        {
            using (var cmd = new SqlCommand("SELECT CASE WHEN COL_LENGTH(@tableName, @columnName) IS NULL THEN 0 ELSE 1 END;", connection, tx))
            {
                cmd.Parameters.AddWithValue("@tableName", tableName);
                cmd.Parameters.AddWithValue("@columnName", columnName);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
            }
        }

        private static void CreateSnapshotTable(SqlConnection connection, SqlTransaction tx, TdfSnapshot snapshot)
        {
            var sql = new StringBuilder();
            sql.Append("CREATE TABLE dbo.").Append(QuoteIdentifier(snapshot.TableName)).AppendLine(" (");
            sql.AppendLine("    RowIndex INT NOT NULL PRIMARY KEY,");
            sql.AppendLine("    ClientTableIndex INT NULL,");
            for (var i = 0; i < snapshot.ColumnCount; i++)
            {
                sql.Append("    ").Append(QuoteIdentifier("Col" + i.ToString("000", CultureInfo.InvariantCulture)))
                   .Append(" NVARCHAR(MAX) NULL");
                sql.AppendLine(i == snapshot.ColumnCount - 1 ? string.Empty : ",");
            }
            sql.AppendLine(");");

            if (snapshot.GlobalBaseIndex.HasValue)
                sql.Append("CREATE UNIQUE INDEX ").Append(QuoteIdentifier("IX_" + snapshot.TableName + "_ClientTableIndex"))
                   .Append(" ON dbo.").Append(QuoteIdentifier(snapshot.TableName)).AppendLine("(ClientTableIndex);");

            using (var cmd = new SqlCommand(sql.ToString(), connection, tx))
            {
                cmd.CommandTimeout = 120;
                cmd.ExecuteNonQuery();
            }
        }

        private static void BulkInsertSnapshot(SqlConnection connection, SqlTransaction tx, TdfSnapshot snapshot)
        {
            var table = new DataTable();
            table.Columns.Add("RowIndex", typeof(int));
            table.Columns.Add("ClientTableIndex", typeof(int));
            for (var i = 0; i < snapshot.ColumnCount; i++)
                table.Columns.Add("Col" + i.ToString("000", CultureInfo.InvariantCulture), typeof(string));

            for (var rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
            {
                var values = new object[snapshot.ColumnCount + 2];
                values[0] = rowIndex;
                values[1] = snapshot.GlobalBaseIndex.HasValue
                    ? (object)checked(snapshot.GlobalBaseIndex.Value + rowIndex)
                    : DBNull.Value;

                var source = snapshot.Rows[rowIndex];
                for (var columnIndex = 0; columnIndex < snapshot.ColumnCount; columnIndex++)
                    values[columnIndex + 2] = (object)source[columnIndex] ?? DBNull.Value;

                table.Rows.Add(values);
            }

            BulkCopy(connection, tx, "dbo." + QuoteIdentifier(snapshot.TableName), table);
        }

        private static void InsertManifest(SqlConnection connection, SqlTransaction tx, TdfSnapshot snapshot)
        {
            const string sql = @"
INSERT INTO dbo.client_tdf_manifest
(FileName, TableName, FileHash, HeaderBytes, VersionMajor, VersionMinor, SourceYear, SourceMonth, SourceDay,
 Flag, HeaderOffset, [ColumnCount], [RowCount], GlobalBaseIndex, ImportedAt)
VALUES
(@FileName, @TableName, @FileHash, @HeaderBytes, @VersionMajor, @VersionMinor, @SourceYear, @SourceMonth, @SourceDay,
 @Flag, @HeaderOffset, @ColumnCount, @RowCount, @GlobalBaseIndex, SYSUTCDATETIME());";

            using (var cmd = new SqlCommand(sql, connection, tx))
            {
                cmd.Parameters.AddWithValue("@FileName", snapshot.FileName);
                cmd.Parameters.AddWithValue("@TableName", snapshot.TableName);
                cmd.Parameters.AddWithValue("@FileHash", snapshot.Hash);
                cmd.Parameters.AddWithValue("@HeaderBytes", snapshot.HeaderBytes);
                cmd.Parameters.AddWithValue("@VersionMajor", snapshot.VersionMajor);
                cmd.Parameters.AddWithValue("@VersionMinor", snapshot.VersionMinor);
                cmd.Parameters.AddWithValue("@SourceYear", snapshot.SourceYear);
                cmd.Parameters.AddWithValue("@SourceMonth", snapshot.SourceMonth);
                cmd.Parameters.AddWithValue("@SourceDay", snapshot.SourceDay);
                cmd.Parameters.AddWithValue("@Flag", (long)snapshot.Flag);
                cmd.Parameters.AddWithValue("@HeaderOffset", (long)snapshot.HeaderOffset);
                cmd.Parameters.AddWithValue("@ColumnCount", snapshot.ColumnCount);
                cmd.Parameters.AddWithValue("@RowCount", snapshot.RowCount);
                cmd.Parameters.AddWithValue("@GlobalBaseIndex", snapshot.GlobalBaseIndex.HasValue ? (object)snapshot.GlobalBaseIndex.Value : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private static int BulkInsertLookup(SqlConnection connection, SqlTransaction tx, TdfSnapshot snapshot)
        {
            var baseName = Path.GetFileName(snapshot.FileName);
            var isItem = string.Equals(baseName, "ItemClient.tdf", StringComparison.OrdinalIgnoreCase);
            var isUseItem = string.Equals(baseName, "UseItemClient.tdf", StringComparison.OrdinalIgnoreCase);
            if ((!isItem && !isUseItem) || !snapshot.GlobalBaseIndex.HasValue)
                return 0;

            var table = new DataTable();
            table.Columns.Add("ClientTableIndex", typeof(int));
            table.Columns.Add("SourceFile", typeof(string));
            table.Columns.Add("SourceTable", typeof(string));
            table.Columns.Add("RowIndex", typeof(int));
            table.Columns.Add("ItemId", typeof(string));
            table.Columns.Add("Category", typeof(string));
            table.Columns.Add("Name", typeof(string));

            for (var rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
            {
                var row = snapshot.Rows[rowIndex];
                string id;
                string category;
                string name;

                if (isItem)
                {
                    category = GetCell(row, 0);
                    id = GetCell(row, 2);
                    name = GetCell(row, 4);
                }
                else
                {
                    id = GetCell(row, 0);
                    category = GetCell(row, 1);
                    name = GetCell(row, 2);
                }

                table.Rows.Add(
                    checked(snapshot.GlobalBaseIndex.Value + rowIndex),
                    snapshot.FileName,
                    snapshot.TableName,
                    rowIndex,
                    DbText(id),
                    DbText(category),
                    DbText(name));
            }

            BulkCopy(connection, tx, "dbo.client_item_lookup", table);
            return table.Rows.Count;
        }

        private static object DbText(string value)
        {
            return value == null ? (object)DBNull.Value : value;
        }

        private static string GetCell(string[] row, int index)
        {
            return row != null && index >= 0 && index < row.Length ? row[index] : null;
        }

        private static void BulkCopy(SqlConnection connection, SqlTransaction tx, string destination, DataTable table)
        {
            if (table.Rows.Count == 0) return;

            using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, tx))
            {
                bulk.DestinationTableName = destination;
                bulk.BatchSize = 2000;
                bulk.BulkCopyTimeout = 300;
                foreach (DataColumn column in table.Columns)
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                bulk.WriteToServer(table);
            }
        }

        private static string BuildUniqueTableName(string fileName, ISet<string> used)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? "Tdf";
            if (stem.EndsWith("_Client", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - "_Client".Length);
            else if (stem.EndsWith("Client", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - "Client".Length);

            var clean = new StringBuilder();
            foreach (var ch in stem)
                clean.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

            var root = "client_" + clean.ToString().Trim('_');
            if (root.Length > 110) root = root.Substring(0, 110);
            var candidate = root;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = root + "_" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
            return candidate;
        }

        private static string QuoteIdentifier(string value)
        {
            return "[" + (value ?? string.Empty).Replace("]", "]]" ) + "]";
        }

        private static string EscapeSqlLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string MakeRelativePath(string root, string path)
        {
            var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            var relative = fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(rootPath.Length)
                : Path.GetFileName(fullPath);
            relative = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (relative.Length > 260)
                throw new InvalidDataException("TDF relative path is longer than 260 characters: " + relative);
            return relative;
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
