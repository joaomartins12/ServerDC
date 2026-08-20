using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class CommandHintEnhancement
    {
        private sealed class CommandHint
        {
            public string Command;
            public string Usage;
            public string Description;
        }

        private static readonly Dictionary<string, CommandHint[]> Hints =
            new Dictionary<string, CommandHint[]>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "Auth",
                    new[]
                    {
                        Hint("/register", "/register <username> <password>", "Create a new account"),
                        Hint("/create", "/create <username> <password>", "Create a new account"),
                        Hint("passwd", "passwd <username> <password>", "Change an account password"),
                        Hint("ban", "ban <username> [days]", "Ban an account"),
                        Hint("unban", "unban <username>", "Unban an account"),
                        Hint("setperm", "setperm <username> <permission>", "Change account permission"),
                        Hint("shutdown", "shutdown <seconds>", "Schedule server shutdown")
                    }
                },
                {
                    "Game",
                    new[]
                    {
                        Hint("/perfprobe", "/perfprobe <field> <value> | /perfprobe off", "Probe StatUpdate fields"),
                        Hint("/money", "/money <character> <amount>", "Give Mito to a character"),
                        Hint("/exp", "/exp <character> <amount>", "Give EXP to a character"),
                        Hint("/weather", "/weather <fine|cloudy|foggy|rain|sunset>", "Change weather")
                    }
                }
            };

        private static CommandHint Hint(string command, string usage, string description)
        {
            return new CommandHint { Command = command, Usage = usage, Description = description };
        }

        public static void Attach(MainForm form)
        {
            if (form == null) return;

            var serversField = typeof(MainForm).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic);
            var servers = serversField == null ? null : serversField.GetValue(form) as IDictionary;
            if (servers == null) return;

            foreach (DictionaryEntry pair in servers)
            {
                var serverName = pair.Key as string;
                if (string.IsNullOrEmpty(serverName)) continue;

                CommandHint[] serverHints;
                if (!Hints.TryGetValue(serverName, out serverHints)) continue;

                var entry = pair.Value;
                if (entry == null) continue;
                var entryType = entry.GetType();
                var commandBoxField = entryType.GetField("CommandBox", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var logPageField = entryType.GetField("LogPage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var commandBox = commandBoxField == null ? null : commandBoxField.GetValue(entry) as TextBox;
                var logPage = logPageField == null ? null : logPageField.GetValue(entry) as Panel;
                if (commandBox == null || logPage == null) continue;

                AttachToCommandBox(commandBox, logPage, serverHints);
            }
        }

        private static void AttachToCommandBox(TextBox commandBox, Panel logPage, CommandHint[] hints)
        {
            var hintLabel = new Label
            {
                AutoSize = true,
                Visible = false,
                BackColor = Color.FromArgb(22, 27, 34),
                ForeColor = Color.FromArgb(232, 235, 239),
                Font = new Font("Consolas", 9F, FontStyle.Regular),
                Padding = new Padding(9, 6, 9, 6),
                MaximumSize = new Size(700, 0)
            };
            logPage.Controls.Add(hintLabel);
            hintLabel.BringToFront();

            Action reposition = delegate
            {
                if (!hintLabel.Visible || commandBox.Parent == null) return;
                var screen = commandBox.Parent.PointToScreen(Point.Empty);
                var local = logPage.PointToClient(screen);
                hintLabel.Location = new Point(
                    Math.Max(8, local.X),
                    Math.Max(8, local.Y - hintLabel.PreferredHeight - 6));
                hintLabel.BringToFront();
            };

            EventHandler refresh = delegate
            {
                var raw = (commandBox.Text ?? string.Empty).TrimStart();
                var token = raw;
                var space = token.IndexOf(' ');
                if (space >= 0) token = token.Substring(0, space);

                CommandHint best = null;
                for (var i = 0; i < hints.Length; i++)
                {
                    var candidate = hints[i];
                    if (candidate.Command.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                        token.StartsWith(candidate.Command, StringComparison.OrdinalIgnoreCase))
                    {
                        best = candidate;
                        break;
                    }
                }

                if (best == null || token.Length == 0)
                {
                    hintLabel.Visible = false;
                    return;
                }

                hintLabel.Text = best.Usage + Environment.NewLine + best.Description;
                hintLabel.Visible = true;
                reposition();
            };

            commandBox.TextChanged += refresh;
            commandBox.Enter += refresh;
            commandBox.Leave += delegate
            {
                if (string.IsNullOrWhiteSpace(commandBox.Text)) hintLabel.Visible = false;
            };
            logPage.Resize += delegate { reposition(); };
        }
    }
}
