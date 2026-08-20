using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace ServerManager
{
    internal static class ClientLauncher
    {
        public const string OfficialServerIp = "61.100.131.48";
        public const string LocalServerIp = "127.0.0.1";
        public const string DefaultOfficialLauncherPath = @"C:\ProgramData\Masangsoft\NpMSLauncher\MSLauncher.exe";
        public static readonly int[] KnownPorts = { 11005, 11011, 11021, 11031, 11041, 11078 };

        private static Process _clientProcess;

        public static bool IsClientRunning
        {
            get
            {
                try
                {
                    if (_clientProcess != null && !_clientProcess.HasExited) return true;
                    var processes = Process.GetProcessesByName("skidrush");
                    try { return processes.Length > 0; }
                    finally { foreach (var process in processes) process.Dispose(); }
                }
                catch { return false; }
            }
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static string ResolveExecutable(string clientFolder, string executablePath)
        {
            var explicitPath = (executablePath ?? string.Empty).Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                if (!Path.IsPathRooted(explicitPath) && !string.IsNullOrWhiteSpace(clientFolder))
                    explicitPath = Path.Combine(clientFolder.Trim().Trim('"'), explicitPath);
                return Path.GetFullPath(explicitPath);
            }

            var folder = (clientFolder ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
            return Path.Combine(Path.GetFullPath(folder), "skidrush.exe");
        }

        public static string ResolveOfficialLauncher(string configuredLauncherPath)
        {
            var configured = (configuredLauncherPath ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(configured)) return DefaultOfficialLauncherPath;
            try { return Path.GetFullPath(configured); }
            catch { return configured; }
        }

        public static string ValidateClient(string clientFolder, string executablePath)
        {
            string exe;
            try { exe = ResolveExecutable(clientFolder, executablePath); }
            catch (Exception ex) { return "Invalid client path: " + ex.Message; }

            if (string.IsNullOrWhiteSpace(exe)) return "Select the Drift City client folder or skidrush.exe first.";
            if (!File.Exists(exe)) return "Client executable not found: " + exe;
            if (!string.Equals(Path.GetFileName(exe), "skidrush.exe", StringComparison.OrdinalIgnoreCase))
                return "Selected executable is not skidrush.exe: " + Path.GetFileName(exe);
            return null;
        }

        public static string ValidateOfficialLauncher(string configuredLauncherPath)
        {
            var launcher = ResolveOfficialLauncher(configuredLauncherPath);
            if (string.IsNullOrWhiteSpace(launcher)) return "Official MSLauncher.exe path is empty.";
            if (!File.Exists(launcher)) return "Official launcher not found: " + launcher;
            if (!string.Equals(Path.GetFileName(launcher), "MSLauncher.exe", StringComparison.OrdinalIgnoreCase))
                return "Selected launcher is not MSLauncher.exe: " + Path.GetFileName(launcher);
            return null;
        }

        public static void EnableRedirect(Action<string> log)
        {
            // IMPORTANT: never modify the host adapter/IP/DHCP configuration again.
            if (log != null)
                log("[Client] Global IP redirect disabled for safety. No Windows network settings were changed.");
        }

        public static void DisableRedirect(Action<string> log)
        {
            if (log != null)
                log("[Client] No global network redirect to remove.");
        }

        public static Process StartClient(string clientFolder, string executablePath, Action<string> log, Action exited)
        {
            return StartClient(clientFolder, executablePath, null, log, exited);
        }

        public static Process StartClient(string clientFolder, string executablePath, string officialLauncherPath, Action<string> log, Action exited)
        {
            var validation = ValidateClient(clientFolder, executablePath);
            if (validation != null) throw new InvalidOperationException(validation);
            if (IsClientRunning) throw new InvalidOperationException("Drift City client is already running.");

            var exe = ResolveExecutable(clientFolder, executablePath);
            var workingDirectory = Path.GetDirectoryName(exe);

            var patchResult = ClientWebPatch.Apply(workingDirectory);
            if (log != null) log("[Client] " + patchResult);

            var launcher = ResolveOfficialLauncher(officialLauncherPath);
            if (File.Exists(launcher))
            {
                if (log != null)
                {
                    log("[Client] Official launch chain selected: MSLauncher.exe -> skidrush.exe");
                    log("[Client] MSLauncher: " + launcher);
                    log("[Client] Expected game: " + exe);
                }

                var launcherInfo = new ProcessStartInfo
                {
                    FileName = launcher,
                    WorkingDirectory = Path.GetDirectoryName(launcher),
                    UseShellExecute = true
                };

                _clientProcess = new Process { StartInfo = launcherInfo, EnableRaisingEvents = true };
                _clientProcess.Exited += delegate
                {
                    if (log != null) log("[Client] MSLauncher.exe exited.");
                    if (exited != null) exited();
                };

                if (!_clientProcess.Start())
                    throw new InvalidOperationException("Windows failed to start MSLauncher.exe.");

                if (log != null) log("[Client] Started MSLauncher.exe PID=" + _clientProcess.Id);
                return _clientProcess;
            }

            if (log != null)
                log("[Client] Official MSLauncher.exe not found; falling back to direct skidrush.exe start.");

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };

            _clientProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _clientProcess.Exited += delegate
            {
                if (log != null) log("[Client] skidrush.exe exited.");
                if (exited != null) exited();
            };

            if (!_clientProcess.Start())
                throw new InvalidOperationException("Windows failed to start skidrush.exe.");

            if (log != null) log("[Client] Started skidrush.exe PID=" + _clientProcess.Id + " from " + workingDirectory);
            return _clientProcess;
        }
    }
}
