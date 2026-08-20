using System;
using System.Collections.Generic;
using System.Security.Principal;

namespace Shared.Util
{
    public class ConsoleUtil
    {
        private const string TitlePrefix = "DCNC: ";

        public static bool UserInteractive
        {
            get
            {
#if __MonoCS__
                return (Console.In is System.IO.StreamReader);
#else
                return Environment.UserInteractive;
#endif
            }
        }

        private static bool HasRealConsole
        {
            get
            {
                try
                {
                    return !Console.IsOutputRedirected && !Console.IsErrorRedirected;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Keeps only the window title behavior. The old ASCII logo, credits and
        /// separator are intentionally suppressed so server output starts directly
        /// with the first Log entry (Server startup requested).
        /// </summary>
        public static void WriteHeader(string consoleTitle, ConsoleColor color)
        {
            if (!HasRealConsole)
                return;

            try
            {
                Console.Title = TitlePrefix + consoleTitle;
            }
            catch
            {
                // No console window attached.
            }
        }

        public static void WriteSeperator()
        {
            // Kept for source compatibility; intentionally no output.
        }

        public static void LoadingTitle()
        {
            if (!HasRealConsole)
                return;

            try
            {
                if (!Console.Title.StartsWith("* "))
                    Console.Title = "* " + Console.Title;
            }
            catch
            {
            }
        }

        public static void RunningTitle()
        {
            if (!HasRealConsole)
                return;

            try
            {
                Console.Title = Console.Title.TrimStart('*', ' ');
            }
            catch
            {
            }
        }

        public static void Exit(int exitCode, bool wait = true)
        {
            bool canReadInput;
            try
            {
                canReadInput = !Console.IsInputRedirected;
            }
            catch
            {
                canReadInput = false;
            }

            if (wait && UserInteractive && canReadInput)
            {
                Log.Info("Press Enter to exit.");
                Console.ReadLine();
            }

            Log.Info("Exiting...");
            Environment.Exit(exitCode);
        }

        public static bool CheckAdmin()
        {
            var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static IList<string> ParseLine(string line)
        {
            var args = new List<string>();
            var quote = false;
            for (int i = 0, n = 0; i <= line.Length; ++i)
            {
                if ((i == line.Length || line[i] == ' ') && !quote)
                {
                    if (i - n > 0)
                        args.Add(line.Substring(n, i - n).Trim(' ', '"'));

                    n = i + 1;
                    continue;
                }

                if (line[i] == '"')
                    quote = !quote;
            }

            return args;
        }
    }
}
