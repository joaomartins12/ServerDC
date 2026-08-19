using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Shared.Network;

namespace Shared.Util
{
    [Flags]
    public enum LogLevel
    {
        Info = 0x0001,
        Warning = 0x0002,
        Error = 0x0004,
        Debug = 0x0008,
        Status = 0x0010,
        Exception = 0x0020,
        Unimplemented = 0x0040,
        None = 0x7FFF
    }

    public static class Log
    {
        private static readonly object FileLock = new object();
        private static string _logFile;
        private static string _serverLogRoot;
        private static string _packetRoot;
        private static string _packetSessionLog;
        private static string _sessionStamp;
        private static string _serverName;
        private static long _packetSequence;
        private static bool _structuredInitialized;

        public static LogLevel Hide { get; set; }
        public static string Archive { private get; set; }

        public static string LogFile
        {
            get { return _logFile; }
            set
            {
                if (value != null)
                {
                    var pathToFile = Path.GetDirectoryName(value);
                    if (!string.IsNullOrEmpty(pathToFile) && !Directory.Exists(pathToFile))
                        Directory.CreateDirectory(pathToFile);

                    if (File.Exists(value))
                    {
                        if (Archive != null)
                        {
                            if (!Directory.Exists(Archive))
                                Directory.CreateDirectory(Archive);

                            var time = File.GetLastWriteTime(value);
                            var archive = Path.Combine(Archive, time.ToString("yyyy-MM-dd_HH-mm"));
                            var archiveFilePath = Path.Combine(archive, Path.GetFileName(value));

                            if (!Directory.Exists(archive))
                                Directory.CreateDirectory(archive);

                            if (File.Exists(archiveFilePath))
                                File.Delete(archiveFilePath);

                            File.Move(value, archiveFilePath);
                        }

                        File.Delete(value);
                    }
                }

                _logFile = value;
            }
        }

        /// <summary>
        /// Initializes the unified Logs folder. The root is located independently by walking
        /// upwards until the server's /system directory is found, so startup messages are also captured.
        /// </summary>
        public static void InitializeStructuredLogging()
        {
            lock (FileLock)
            {
                if (_structuredInitialized)
                    return;

                _serverName = Process.GetCurrentProcess().ProcessName;
                _sessionStamp = DateTime.Now.ToString("HH-mm-ss") + "_pid" + Process.GetCurrentProcess().Id;

                var serverRoot = FindServerRoot();
                var dayRoot = Path.Combine(serverRoot, "Logs", DateTime.Now.ToString("yyyy-MM-dd"));
                _serverLogRoot = Path.Combine(dayRoot, SafeFileName(_serverName));
                _packetRoot = Path.Combine(_serverLogRoot, "Packets");

                Directory.CreateDirectory(_serverLogRoot);
                Directory.CreateDirectory(_packetRoot);
                Directory.CreateDirectory(Path.Combine(_packetRoot, "IN"));
                Directory.CreateDirectory(Path.Combine(_packetRoot, "OUT"));

                _logFile = Path.Combine(_serverLogRoot, "server_" + _sessionStamp + ".log");
                _packetSessionLog = Path.Combine(_packetRoot, "packets_" + _sessionStamp + ".log");
                _structuredInitialized = true;

                File.AppendAllText(_logFile,
                    "===== Drift City Server Log =====" + Environment.NewLine +
                    "Server: " + _serverName + Environment.NewLine +
                    "Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine +
                    "PID: " + Process.GetCurrentProcess().Id + Environment.NewLine +
                    "Root: " + serverRoot + Environment.NewLine +
                    "=================================" + Environment.NewLine,
                    Encoding.UTF8);

                File.AppendAllText(_packetSessionLog,
                    "===== Drift City Packet Session =====" + Environment.NewLine +
                    "Server: " + _serverName + Environment.NewLine +
                    "Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine +
                    "Format: SEQ | TIMESTAMP | DIR | PORT | ID | NAME | WIRE_BYTES | ENDPOINT | USER | CHARACTER" + Environment.NewLine +
                    "=====================================" + Environment.NewLine,
                    Encoding.UTF8);
            }
        }

        private static string FindServerRoot()
        {
            try
            {
                var current = new DirectoryInfo(Environment.CurrentDirectory);
                for (var i = 0; i < 5 && current != null; i++, current = current.Parent)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, "system")))
                        return current.FullName;
                }
            }
            catch
            {
            }

            return Environment.CurrentDirectory;
        }

        /// <summary>
        /// Stores one exact packet as seen on the TCP wire. The supplied buffer must include
        /// the two-byte packet length and two-byte packet id before the packet body.
        /// </summary>
        public static void PacketTrace(string direction, int port, ushort id, byte[] wireBytes,
            string endpoint = null, string username = null, string characterName = null)
        {
            try
            {
                if (!_structuredInitialized)
                    InitializeStructuredLogging();

                if (wireBytes == null)
                    wireBytes = new byte[0];

                var sequence = Interlocked.Increment(ref _packetSequence);
                var now = DateTime.Now;
                var dir = string.Equals(direction, "OUT", StringComparison.OrdinalIgnoreCase) ? "OUT" : "IN";
                var packetName = GetPacketName(id);
                var fileBase = sequence.ToString("D6") + "_" + now.ToString("HH-mm-ss.fff") +
                               "_ID" + id.ToString("D4") + "_" + SafeFileName(packetName);
                var dirRoot = Path.Combine(_packetRoot, dir);
                var txtPath = Path.Combine(dirRoot, fileBase + ".txt");
                var binPath = Path.Combine(dirRoot, fileBase + ".bin");

                var body = new StringBuilder();
                body.AppendLine("DRIFT CITY PACKET CAPTURE");
                body.AppendLine("=========================");
                body.AppendLine("Sequence   : " + sequence);
                body.AppendLine("Timestamp  : " + now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                body.AppendLine("Direction  : " + dir);
                body.AppendLine("Server     : " + _serverName);
                body.AppendLine("Port       : " + port);
                body.AppendLine("Packet ID  : " + id + " (0x" + id.ToString("X") + ")");
                body.AppendLine("Packet Name: " + packetName);
                body.AppendLine("Wire Bytes : " + wireBytes.Length);
                body.AppendLine("Endpoint   : " + (endpoint ?? ""));
                body.AppendLine("User       : " + (username ?? ""));
                body.AppendLine("Character  : " + (characterName ?? ""));
                body.AppendLine();
                body.AppendLine("HEX DUMP");
                body.AppendLine("--------");
                body.AppendLine(BinaryWriterExt.HexDump(wireBytes));

                lock (FileLock)
                {
                    Directory.CreateDirectory(dirRoot);
                    File.WriteAllText(txtPath, body.ToString(), Encoding.UTF8);
                    File.WriteAllBytes(binPath, wireBytes);

                    using (var writer = new StreamWriter(_packetSessionLog, true, Encoding.UTF8))
                    {
                        writer.WriteLine("{0:D6} | {1} | {2} | {3} | {4} (0x{4:X}) | {5} | {6} | {7} | {8} | {9}",
                            sequence,
                            now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                            dir,
                            port,
                            id,
                            packetName,
                            wireBytes.Length,
                            endpoint ?? "",
                            username ?? "",
                            characterName ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug("PacketTrace failed for id {0}: {1}", id, ex.Message);
            }
        }

        private static string GetPacketName(ushort id)
        {
            try
            {
                var name = Packets.GetName(id);
#if DEBUG
                if (DefaultServer.PacketNameDatabase != null && DefaultServer.PacketNameDatabase.ContainsKey(id))
                    return DefaultServer.PacketNameDatabase[id] + "_" + name;
#endif
                return name;
            }
            catch
            {
                return "UnknownPacket";
            }
        }

        private static string SafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unknown";

            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');

            return value.Replace(' ', '_');
        }

        public static void Info(string format, params object[] args)
        {
            WriteLine(LogLevel.Info, format, args);
        }

        public static void Warning(string format, params object[] args)
        {
            WriteLine(LogLevel.Warning, format, args);
        }

        public static void Error(string format, params object[] args)
        {
            WriteLine(LogLevel.Error, format, args);
        }

        public static void Debug(string format, params object[] args)
        {
#if DEBUG
            WriteLine(LogLevel.Debug, format, args);
#endif
        }

        public static void Debug(object obj)
        {
            WriteLine(LogLevel.Debug, obj.ToString());
        }

        public static void Status(string format, params object[] args)
        {
            WriteLine(LogLevel.Status, format, args);
        }

        public static void Exception(Exception ex, string description = null, params object[] args)
        {
            if (description != null)
            {
                if (Hide.HasFlag(LogLevel.Exception))
                    description += " See log file for more details.";

                WriteLine(LogLevel.Error, description, args);
            }

            WriteLine(LogLevel.Exception, ex.ToString());
        }

        public static void Unimplemented(string format, params object[] args)
        {
            WriteLine(LogLevel.Unimplemented, format, args);
        }

        public static void Progress(int current, int max)
        {
            var donePerc = 100f / max * current;
            var done = (int)Math.Min(20, Math.Ceiling(20f / max * current));

            Write(LogLevel.Info, false, "[" + "".PadRight(done, '#') + "".PadLeft(20 - done, '.') + "] {0,5:0.0}%\r",
                donePerc);
        }

        public static void WriteLine(LogLevel level, string format, params object[] args)
        {
            Write(level, format + Environment.NewLine, args);
        }

        public static void WriteLine()
        {
            WriteLine(LogLevel.None, "");
        }

        public static void Write(LogLevel level, string format, params object[] args)
        {
            Write(level, true, format, args);
        }

        private static void Write(LogLevel level, bool toFile, string format, params object[] args)
        {
            if (!_structuredInitialized && toFile)
            {
                try { InitializeStructuredLogging(); }
                catch { }
            }

            lock (Console.Out)
            {
                if (!Hide.HasFlag(level))
                {
                    try
                    {
                        switch (level)
                        {
                            case LogLevel.Info: Console.ForegroundColor = ConsoleColor.White; break;
                            case LogLevel.Warning: Console.ForegroundColor = ConsoleColor.Yellow; break;
                            case LogLevel.Error: Console.ForegroundColor = ConsoleColor.Red; break;
                            case LogLevel.Debug: Console.ForegroundColor = ConsoleColor.Cyan; break;
                            case LogLevel.Status: Console.ForegroundColor = ConsoleColor.Green; break;
                            case LogLevel.Exception: Console.ForegroundColor = ConsoleColor.DarkRed; break;
                            case LogLevel.Unimplemented: Console.ForegroundColor = ConsoleColor.DarkGray; break;
                        }
                    }
                    catch { }

                    if (level != LogLevel.None)
                        Console.Write("[{0}]", level);

                    try { Console.ForegroundColor = ConsoleColor.Gray; } catch { }

                    if (level != LogLevel.None)
                        Console.Write(" - ");

                    Console.Write(format, args);
                }
            }

            if (_logFile == null || !toFile)
                return;

            lock (FileLock)
            {
                using (var file = new StreamWriter(_logFile, true, Encoding.UTF8))
                {
                    file.Write("{0:yyyy-MM-dd HH:mm:ss.fff} ", DateTime.Now);
                    if (level != LogLevel.None)
                        file.Write("[{0}] - ", level);
                    file.Write(format, args);
                }
            }
        }
    }
}
