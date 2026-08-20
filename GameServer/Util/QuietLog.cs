using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace GameServer.Util
{
    /// <summary>
    /// File-only diagnostic/audit logger for verbose flows that are already stable.
    /// Nothing written here is sent to stdout, so DCServerManager stays readable while
    /// the detailed history remains available under Logs for later research.
    /// </summary>
    internal static class QuietLog
    {
        private static readonly object Sync = new object();

        public static void Write(string category, string format, params object[] args)
        {
            try
            {
                var root = AppDomain.CurrentDomain.BaseDirectory;
                var dayRoot = Path.Combine(root, "Logs", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                var folder = Path.Combine(dayRoot, "GameServer", "Audit");
                Directory.CreateDirectory(folder);

                var safeCategory = SafeFileName(string.IsNullOrWhiteSpace(category) ? "General" : category);
                var path = Path.Combine(folder, safeCategory + ".txt");
                var message = args == null || args.Length == 0
                    ? format
                    : string.Format(CultureInfo.InvariantCulture, format, args);

                lock (Sync)
                {
                    File.AppendAllText(
                        path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " - " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Audit logging must never affect gameplay.
            }
        }

        private static string SafeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value.Replace(' ', '_');
        }
    }
}
