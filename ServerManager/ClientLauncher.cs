using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace ServerManager
{
    internal static class ClientLauncher
    {
        public const string OfficialServerIp = "61.100.131.48";
        public const string LocalServerIp = "127.0.0.1";
        public static readonly int[] KnownPorts = { 11005, 11011, 11021, 11031, 11041, 11078 };

        private static Process _clientProcess;
        private static bool _redirectOwnedByManager;

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
            if (!IsAdministrator())
                throw new InvalidOperationException("GAME START redirect requires DCServerManager to be started as Administrator.");

            if (HasLocalOfficialAddress())
            {
                if (log != null) log("[Client] Redirect address " + OfficialServerIp + " is already local.");
                _redirectOwnedByManager = false;
                return;
            }

            var script =
                "$route = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop | " +
                "Where-Object { $_.NextHop -ne '0.0.0.0' } | Sort-Object RouteMetric,InterfaceMetric | Select-Object -First 1; " +
                "if ($null -eq $route) { throw 'No active IPv4 default route found.' }; " +
                "$existing = Get-NetIPAddress -AddressFamily IPv4 -IPAddress '" + OfficialServerIp + "' -ErrorAction SilentlyContinue; " +
                "if ($null -eq $existing) { New-NetIPAddress -InterfaceIndex $route.InterfaceIndex -IPAddress '" + OfficialServerIp + "' -PrefixLength 32 -SkipAsSource $true -ErrorAction Stop | Out-Null };";

            RunPowerShell(script);
            if (!HasLocalOfficialAddress())
                throw new InvalidOperationException("Windows did not register the local redirect address " + OfficialServerIp + ".");

            _redirectOwnedByManager = true;
            if (log != null)
            {
                log("[Client] Network redirect ACTIVE: " + OfficialServerIp + " -> local machine");
                log("[Client] Known ports: " + string.Join(", ", KnownPorts.Select(p => p.ToString()).ToArray()));
            }
        }

        public static void DisableRedirect(Action<string> log)
        {
            if (!_redirectOwnedByManager) return;
            if (!IsAdministrator()) return;

            try
            {
                var script =
                    "Get-NetIPAddress -AddressFamily IPv4 -IPAddress '" + OfficialServerIp + "' -ErrorAction SilentlyContinue | " +
                    "Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue;";
                RunPowerShell(script);
                _redirectOwnedByManager = false;
                if (log != null) log("[Client] Network redirect removed.");
            }
            catch (Exception ex)
            {
                if (log != null) log("[Client] Failed to remove redirect: " + ex.Message);
            }
        }

        public static Process StartClient(string clientFolder, string executablePath, Action<string> log, Action exited)
        {
            var validation = ValidateClient(clientFolder, executablePath);
            if (validation != null) throw new InvalidOperationException(validation);
            if (IsClientRunning) throw new InvalidOperationException("Drift City client is already running from ServerManager.");

            var exe = ResolveExecutable(clientFolder, executablePath);
            var workingDirectory = Path.GetDirectoryName(exe);
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

        private static bool HasLocalOfficialAddress()
        {
            try
            {
                var output = RunPowerShell(
                    "$x = Get-NetIPAddress -AddressFamily IPv4 -IPAddress '" + OfficialServerIp + "' -ErrorAction SilentlyContinue; " +
                    "if ($null -ne $x) { 'YES' } else { 'NO' }");
                return output.IndexOf("YES", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static string RunPowerShell(string script)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("Unable to start PowerShell for client redirect.");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(15000);
                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Timed out while configuring the client network redirect.");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "PowerShell redirect command failed." : stderr.Trim());
                return stdout ?? string.Empty;
            }
        }
    }
}
