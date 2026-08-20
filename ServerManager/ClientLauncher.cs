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
        public static readonly int[] KnownPorts = { 11005, 11011, 11021, 11031, 11041, 11078 };

        private static Process _clientProcess;

        public static bool IsClientRunning
        {
            get
            {
                try { return _clientProcess != null && !_clientProcess.HasExited; }
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

        public static void EnableRedirect(Action<string> log)
        {
            // IMPORTANT: Do not add the official address to a Windows network adapter.
            // New-NetIPAddress can disable DHCP on a DHCP-managed interface, which can
            // remove the default gateway and break the host's Internet connectivity.
            // Redirecting the Korean client must be implemented process-locally instead.
            if (log != null)
                log("[Client] Global IP redirect disabled for safety. No Windows network settings were changed.");
        }

        public static void DisableRedirect(Action<string> log)
        {
            // Kept for compatibility with the existing UI/cleanup flow. The manager no
            // longer owns or changes a global network redirect.
            if (log != null)
                log("[Client] No global network redirect to remove.");
        }

        public static Process StartClient(string clientFolder, string executablePath, Action<string> log, Action exited)
        {
            var validation = ValidateClient(clientFolder, executablePath);
            if (validation != null) throw new InvalidOperationException(validation);
            if (IsClientRunning) throw new InvalidOperationException("Drift City client is already running from ServerManager.");

            var exe = ResolveExecutable(clientFolder, executablePath);
            var workingDirectory = Path.GetDirectoryName(exe);

            var patchResult = ClientWebPatch.Apply(workingDirectory);
            if (log != null) log("[Client] " + patchResult);

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
