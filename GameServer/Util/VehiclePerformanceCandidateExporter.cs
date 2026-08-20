using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Shared.Objects;

namespace GameServer.Util
{
    internal static class VehiclePerformanceCandidateExporter
    {
        internal sealed class ExportResult
        {
            public int TablesScanned;
            public int RowsScanned;
            public int CandidateRows;
            public string CandidatePath;
            public string ProfilePath;
        }

        private sealed class Target
        {
            public string Name;
            public double Value;
        }

        private sealed class ColumnProfile
        {
            public long Rows;
            public long NumericRows;
            public double Min = double.MaxValue;
            public double Max = double.MinValue;
            public double Sum;
            public readonly HashSet<string> Samples = new HashSet<string>(StringComparer.Ordinal);
        }

        public static ExportResult Export(Character character, Vehicle vehicle, ResolvedVehicleStats stats, EquippedItemStats equipped)
        {
            if (character == null) throw new ArgumentNullException("character");
            if (vehicle == null) throw new ArgumentNullException("vehicle");
            if (stats == null) throw new ArgumentNullException("stats");
            if (equipped == null) throw new ArgumentNullException("equipped");

            var user = (int)character.Level;
            var totalSpeed = stats.Speed + equipped.Speed + user;
            var totalCrash = stats.Crash + equipped.Crash + user;
            var totalAccel = stats.Accel + equipped.Accel + user;
            var totalBoost = stats.Boost + equipped.Boost + user;

            var targets = new List<Target>
            {
                new Target { Name = "VehicleId", Value = stats.VehicleId },
                new Target { Name = "Grade", Value = stats.Grade },
                new Target { Name = "BaseSpeed", Value = stats.Speed },
                new Target { Name = "BaseCrash", Value = stats.Crash },
                new Target { Name = "BaseAccel", Value = stats.Accel },
                new Target { Name = "BaseBoost", Value = stats.Boost },
                new Target { Name = "PartSpeed", Value = equipped.Speed },
                new Target { Name = "PartCrash", Value = equipped.Crash },
                new Target { Name = "PartAccel", Value = equipped.Accel },
                new Target { Name = "PartBoost", Value = equipped.Boost },
                new Target { Name = "TotalSpeed", Value = totalSpeed },
                new Target { Name = "TotalCrash", Value = totalCrash },
                new Target { Name = "TotalAccel", Value = totalAccel },
                new Target { Name = "TotalBoost", Value = totalBoost }
            };

            var root = AppDomain.CurrentDomain.BaseDirectory;
            var folder = Path.Combine(root, "Logs", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "GameServer", "Research");
            Directory.CreateDirectory(folder);
            var candidatePath = Path.Combine(folder, "VehiclePerformanceCandidates.csv");
            var profilePath = Path.Combine(folder, "VehiclePerformanceNumericProfile.csv");

            var cs = new SqlConnectionStringBuilder
            {
                DataSource = "localhost",
                InitialCatalog = "DCServer",
                IntegratedSecurity = true,
                TrustServerCertificate = true,
                Encrypt = false,
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                ApplicationName = "DriftCity Vehicle Performance Research"
            }.ConnectionString;

            var candidate = new StringBuilder();
            candidate.AppendLine("Table,RowIndex,ClientTableIndex,Score,Matches,VehicleNameMatch,Values");
            var profile = new StringBuilder();
            profile.AppendLine("Table,Column,Rows,NumericRows,NumericPercent,Min,Max,Average,Samples");

            var tablesScanned = 0;
            var rowsScanned = 0;
            var candidateRows = 0;

            using (var connection = new SqlConnection(cs))
            {
                connection.Open();
                var tables = ReadImportedTables(connection);

                foreach (var table in tables)
                {
                    var columns = ReadColumns(connection, table);
                    if (columns.Count == 0) continue;

                    tablesScanned++;
                    var profiles = columns.ToDictionary(x => x, x => new ColumnProfile(), StringComparer.OrdinalIgnoreCase);
                    var selected = string.Join(",", columns.Select(QuoteName));
                    var sql = "SELECT " + selected + " FROM dbo." + QuoteName(table) + ";";

                    using (var command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 120;
                        using (var reader = command.ExecuteReader())
                        {
                            var emittedForTable = 0;
                            while (reader.Read())
                            {
                                rowsScanned++;
                                var values = new string[columns.Count];
                                var matches = new List<string>();
                                var matchColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                var vehicleNameMatch = false;

                                for (var i = 0; i < columns.Count; i++)
                                {
                                    var raw = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
                                    values[i] = raw;

                                    ColumnProfile p;
                                    if (profiles.TryGetValue(columns[i], out p))
                                    {
                                        p.Rows++;
                                        double numeric;
                                        if (TryNumber(raw, out numeric))
                                        {
                                            p.NumericRows++;
                                            if (numeric < p.Min) p.Min = numeric;
                                            if (numeric > p.Max) p.Max = numeric;
                                            p.Sum += numeric;
                                            if (p.Samples.Count < 8) p.Samples.Add(raw);
                                        }
                                    }

                                    if (!string.IsNullOrWhiteSpace(stats.VehicleName) && raw.IndexOf(stats.VehicleName, StringComparison.OrdinalIgnoreCase) >= 0)
                                        vehicleNameMatch = true;

                                    double number;
                                    if (!TryNumber(raw, out number)) continue;
                                    foreach (var target in targets)
                                    {
                                        if (Math.Abs(number - target.Value) > 0.0001) continue;
                                        matches.Add(target.Name + "@" + columns[i]);
                                        matchColumns.Add(columns[i]);
                                    }
                                }

                                var strongStatMatches = matches.Count(x =>
                                    x.StartsWith("Base", StringComparison.Ordinal) || x.StartsWith("Total", StringComparison.Ordinal));
                                var identityMatch = matches.Any(x => x.StartsWith("VehicleId@", StringComparison.Ordinal));
                                var score = strongStatMatches * 3 + (identityMatch ? 2 : 0) + (vehicleNameMatch ? 5 : 0) + Math.Min(2, matches.Count);

                                if (score < 3 || emittedForTable >= 500) continue;

                                emittedForTable++;
                                candidateRows++;
                                candidate.Append(Csv(table)).Append(',')
                                    .Append(Csv(ValueOf(columns, values, "RowIndex"))).Append(',')
                                    .Append(Csv(ValueOf(columns, values, "ClientTableIndex"))).Append(',')
                                    .Append(score.ToString(CultureInfo.InvariantCulture)).Append(',')
                                    .Append(Csv(string.Join("|", matches.Distinct()))).Append(',')
                                    .Append(vehicleNameMatch ? "1" : "0").Append(',')
                                    .Append(Csv(CompactValues(columns, values, matchColumns)))
                                    .AppendLine();
                            }
                        }
                    }

                    foreach (var column in columns)
                    {
                        var p = profiles[column];
                        if (p.NumericRows == 0) continue;
                        profile.Append(Csv(table)).Append(',')
                            .Append(Csv(column)).Append(',')
                            .Append(p.Rows.ToString(CultureInfo.InvariantCulture)).Append(',')
                            .Append(p.NumericRows.ToString(CultureInfo.InvariantCulture)).Append(',')
                            .Append((p.Rows == 0 ? 0.0 : (100.0 * p.NumericRows / p.Rows)).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                            .Append(p.Min.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(p.Max.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append((p.Sum / p.NumericRows).ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(Csv(string.Join("|", p.Samples)))
                            .AppendLine();
                    }
                }
            }

            var header = new StringBuilder();
            header.AppendLine("# Vehicle performance candidate research");
            header.AppendLine("# Generated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            header.AppendLine("# VehicleId=" + stats.VehicleId + " Name=" + (stats.VehicleName ?? "") + " Grade=V" + stats.Grade);
            header.AppendLine("# Base S=" + stats.Speed + " C=" + stats.Crash + " A=" + stats.Accel + " B=" + stats.Boost);
            header.AppendLine("# Parts S=" + equipped.Speed + " C=" + equipped.Crash + " A=" + equipped.Accel + " B=" + equipped.Boost);
            header.AppendLine("# User=" + user + " Total S=" + totalSpeed + " C=" + totalCrash + " A=" + totalAccel + " B=" + totalBoost);

            File.WriteAllText(candidatePath, header.ToString() + candidate, Encoding.UTF8);
            File.WriteAllText(profilePath, header.ToString() + profile, Encoding.UTF8);

            QuietLog.Write("VehiclePerformanceResearch", "Candidate scan complete tables={0} rows={1} candidates={2} file={3}",
                tablesScanned, rowsScanned, candidateRows, candidatePath);

            return new ExportResult
            {
                TablesScanned = tablesScanned,
                RowsScanned = rowsScanned,
                CandidateRows = candidateRows,
                CandidatePath = candidatePath,
                ProfilePath = profilePath
            };
        }

        private static List<string> ReadImportedTables(SqlConnection connection)
        {
            var result = new List<string>();
            const string sql = @"
IF OBJECT_ID('dbo.client_tdf_manifest','U') IS NOT NULL
    SELECT DISTINCT TableName FROM dbo.client_tdf_manifest WHERE TableName IS NOT NULL ORDER BY TableName;";
            using (var command = new SqlCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var table = Convert.ToString(reader[0], CultureInfo.InvariantCulture);
                    if (IsSafeTableName(table)) result.Add(table);
                }
            }
            return result;
        }

        private static List<string> ReadColumns(SqlConnection connection, string table)
        {
            var result = new List<string>();
            const string sql = @"
SELECT c.name
FROM sys.columns c
JOIN sys.tables t ON t.object_id=c.object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name='dbo' AND t.name=@table
ORDER BY c.column_id;";
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@table", table);
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(Convert.ToString(reader[0], CultureInfo.InvariantCulture));
            }
            return result;
        }

        private static bool IsSafeTableName(string table)
        {
            if (string.IsNullOrWhiteSpace(table) || !table.StartsWith("client_", StringComparison.OrdinalIgnoreCase)) return false;
            for (var i = 0; i < table.Length; i++)
            {
                var c = table[i];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            }
            return true;
        }

        private static string QuoteName(string name)
        {
            return "[" + (name ?? string.Empty).Replace("]", "]]" ) + "]";
        }

        private static bool TryNumber(string raw, out double value)
        {
            return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
        }

        private static string ValueOf(IList<string> columns, IList<string> values, string name)
        {
            for (var i = 0; i < columns.Count; i++)
                if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return values[i];
            return string.Empty;
        }

        private static string CompactValues(IList<string> columns, IList<string> values, ISet<string> matched)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.IsNullOrEmpty(values[i])) continue;
                if (sb.Length > 0) sb.Append(" | ");
                if (matched.Contains(columns[i])) sb.Append('*');
                sb.Append(columns[i]).Append('=').Append(values[i].Replace("\r", " ").Replace("\n", " "));
            }
            return sb.ToString();
        }

        private static string Csv(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
