using System;
using System.IO;
using System.Security.Cryptography;

namespace ServerManager
{
    internal static class ClientWebPatch
    {
        private const string DllName = "HanDxWebForClient.dll";
        private const string BackupSuffix = ".official-backup";

        // Korean client DLL supplied with the current Masang build.
        private const long ExpectedSize = 290816;
        private const string ExpectedSha256 = "EA1BB2E7764FB2C8DC7FADE6C4377ADC7DB768EE1347A353B07C8362154F9A46";

        private const int HanDxWebInitOffset = 0xEDF0;
        private const int HanDxWebInitWithUserInfoOffset = 0xF020;

        private static readonly byte[] HanDxWebInitOriginalPrefix =
            { 0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0x80, 0x02, 0x03, 0x10 };

        private static readonly byte[] HanDxWebInitWithUserInfoOriginalPrefix =
            { 0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0xA0, 0x02, 0x03, 0x10 };

        // xor eax,eax ; ret 0x0C / ret 0x18
        private static readonly byte[] HanDxWebInitPatch = { 0x33, 0xC0, 0xC2, 0x0C, 0x00 };
        private static readonly byte[] HanDxWebInitWithUserInfoPatch = { 0x33, 0xC0, 0xC2, 0x18, 0x00 };

        public static string Apply(string clientFolder)
        {
            if (string.IsNullOrWhiteSpace(clientFolder))
                throw new InvalidOperationException("Client folder is not configured.");

            var dllPath = Path.Combine(clientFolder, DllName);
            if (!File.Exists(dllPath))
                throw new FileNotFoundException(DllName + " was not found in the selected client folder.", dllPath);

            var bytes = File.ReadAllBytes(dllPath);
            if (IsPatched(bytes))
                return DllName + " homepage patch is already active.";

            if (bytes.LongLength != ExpectedSize)
                throw new InvalidOperationException(DllName + " has an unexpected size (" + bytes.LongLength + "). No patch was applied.");

            var hash = ComputeSha256(bytes);
            if (!string.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(DllName + " does not match the verified Korean client build. SHA-256=" + hash + ". No patch was applied.");

            if (!Matches(bytes, HanDxWebInitOffset, HanDxWebInitOriginalPrefix) ||
                !Matches(bytes, HanDxWebInitWithUserInfoOffset, HanDxWebInitWithUserInfoOriginalPrefix))
                throw new InvalidOperationException("Verified homepage function signatures were not found. No patch was applied.");

            var backupPath = dllPath + BackupSuffix;
            if (!File.Exists(backupPath))
                File.Copy(dllPath, backupPath, false);

            Write(bytes, HanDxWebInitOffset, HanDxWebInitPatch);
            Write(bytes, HanDxWebInitWithUserInfoOffset, HanDxWebInitWithUserInfoPatch);
            File.WriteAllBytes(dllPath, bytes);

            return "Homepage launch patch applied to " + DllName + ". Backup: " + Path.GetFileName(backupPath);
        }

        public static string Restore(string clientFolder)
        {
            if (string.IsNullOrWhiteSpace(clientFolder))
                throw new InvalidOperationException("Client folder is not configured.");

            var dllPath = Path.Combine(clientFolder, DllName);
            var backupPath = dllPath + BackupSuffix;
            if (!File.Exists(backupPath))
                return "No homepage patch backup was found.";

            File.Copy(backupPath, dllPath, true);
            return DllName + " restored from the official backup.";
        }

        public static bool IsPatched(string clientFolder)
        {
            if (string.IsNullOrWhiteSpace(clientFolder)) return false;
            var dllPath = Path.Combine(clientFolder, DllName);
            if (!File.Exists(dllPath)) return false;
            try { return IsPatched(File.ReadAllBytes(dllPath)); }
            catch { return false; }
        }

        private static bool IsPatched(byte[] bytes)
        {
            return Matches(bytes, HanDxWebInitOffset, HanDxWebInitPatch) &&
                   Matches(bytes, HanDxWebInitWithUserInfoOffset, HanDxWebInitWithUserInfoPatch);
        }

        private static bool Matches(byte[] bytes, int offset, byte[] expected)
        {
            if (bytes == null || expected == null || offset < 0 || offset + expected.Length > bytes.Length)
                return false;
            for (var i = 0; i < expected.Length; i++)
                if (bytes[offset + i] != expected[i]) return false;
            return true;
        }

        private static void Write(byte[] bytes, int offset, byte[] patch)
        {
            Buffer.BlockCopy(patch, 0, bytes, offset, patch.Length);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }
    }
}
