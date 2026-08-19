using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;

namespace Shared.Util
{
    public class ConsoleUtil
    {
        private const string TitlePrefix = "DCNC: ";
        private const int FallbackConsoleWidth = 100;

        private static readonly string[] Logo =
        {
            @" /$$$$$$$   /$$$$$$  /$$   /$$  /$$$$$$ ",
            @"| $$__  $$ /$$__  $$| $$$ | $$ /$$__  $$",
            @"| $$  \ $$| $$  \__/| $$$$| $$| $$  \__/",
            @"| $$  | $$| $$      | $$ $$ $$| $$      ",
            @"| $$  | $$| $$      | $$  $$$$| $$      ",
            @"| $$  | $$| $$    $$| $$\  $$$| $$    $$",
            @"| $$$$$$$/|  $$$$$$/| $$ \  $$|  $$$$$$/",
            @"|_______/  \______/ |__/  \__/ \______/ "
        };

        private static readonly string[] Credits =
        {
            @"Copyright (c) 2017 GigaToni",
            @"For problems & support: https://github.com/exmex/DCNC/issues",
            @"Also visit our discord channel: https://discord.gg/GnW6xxf",
            @"Special Thanks to amPerl"
        };

        /// <summary>
        ///     Gets a value indicating whether the current process is running
        ///     in user interactive mode.
        /// </summary>
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

        /// <summary>
        /// True when the process owns a real console instead of being hosted by
        /// DCServerManager (or another process) with redirected stdin/stdout.
        /// </summary>
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

        private static int GetConsoleWidthSafe()
        {
            if (!HasRealConsole)
                return FallbackConsoleWidth;

            try
            {
                return Math.Max(40, Console.WindowWidth);
            }
            catch
            {
                return FallbackConsoleWidth;
            }
        }

        private static void SetConsoleColorSafe(ConsoleColor color)
        {
            if (!HasRealConsole)
                return;

            try
            {
                Console.ForegroundColor = color;
            }
            catch
            {
                // Output may be redirected or the process may not own a console.
            }
        }

        private static void ResetConsoleColorSafe()
        {
            if (!HasRealConsole)
                return;

            try
            {
                Console.ResetColor();
            }
            catch
            {
                // Output may be redirected or the process may not own a console.
            }
        }

        /// <summary>
        ///     Writes logo and credits to Console. When hosted by ServerManager,
        ///     console-only operations such as WindowWidth/Title/Color are skipped.
        /// </summary>
        public static void WriteHeader(string consoleTitle, ConsoleColor color)
        {
            if (HasRealConsole)
            {
                try
                {
                    Console.Title = TitlePrefix + consoleTitle;
                }
                catch
                {
                    // Ignore environments without a console window.
                }
            }

            Console.WriteLine();

            SetConsoleColorSafe(color);
            WriteLinesCentered(Logo);

            Console.WriteLine();

            SetConsoleColorSafe(ConsoleColor.White);
            WriteLinesCentered(Credits);

            ResetConsoleColorSafe();
            WriteSeperator();
        }

        /// <summary>
        ///     Writes a separator using the real console width when available,
        ///     or a fixed safe width when output is redirected to ServerManager.
        /// </summary>
        public static void WriteSeperator()
        {
            Console.WriteLine("".PadLeft(GetConsoleWidthSafe(), '_'));
        }

        private static void WriteLinesCentered(string[] lines)
        {
            var longestLine = lines.Max(a => a.Length);
            foreach (var line in lines)
                WriteLineCentered(line, longestLine);
        }

        private static void WriteLineCentered(string line, int referenceLength = -1)
        {
            if (referenceLength < 0)
                referenceLength = line.Length;

            var width = GetConsoleWidthSafe();
            var padding = line.Length + width / 2 - referenceLength / 2;
            if (padding < line.Length)
                padding = line.Length;

            Console.WriteLine(line.PadLeft(padding));
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
                // No console window attached.
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
                // No console window attached.
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
