using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class OfficialLauncherSettings
    {
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerManager.launcher.settings");
        private static TextBox _pathBox;
        private static Label _status;

        public static string LauncherPath { get; private set; }

        public static void Attach(MainForm form)
        {
            LauncherPath = LoadPath();
            var settingsPage = FindSettingsPage(form);
            if (settingsPage == null) return;

            var card = new Panel
            {
                BackColor = Color.FromArgb(20, 24, 30),
                Location = new Point(950, 92),
                Size = new Size(340, 245),
                Padding = new Padding(18)
            };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(45, 51, 60)))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            card.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "OFFICIAL LAUNCHER",
                ForeColor = Color.FromArgb(232, 235, 239),
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Location = new Point(18, 16)
            });

            card.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "GAME START reproduces the official chain MSLauncher.exe → skidrush.exe. No command-line arguments are required.",
                ForeColor = Color.FromArgb(133, 142, 155),
                Font = new Font("Segoe UI", 8.8F),
                Location = new Point(18, 45),
                Size = new Size(300, 45)
            });

            _pathBox = new TextBox
            {
                Text = LauncherPath,
                Location = new Point(18, 98),
                Size = new Size(300, 27),
                BackColor = Color.FromArgb(12, 15, 19),
                ForeColor = Color.FromArgb(232, 235, 239),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 8.5F)
            };
            _pathBox.TextChanged += delegate
            {
                LauncherPath = _pathBox.Text.Trim();
                SavePath();
                RefreshStatus();
            };
            card.Controls.Add(_pathBox);

            var browse = MakeButton("BROWSE", new Point(18, 136), 90);
            browse.Click += delegate
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "Select MasangSoft MSLauncher.exe";
                    dialog.Filter = "MasangSoft launcher|MSLauncher.exe|Executable files|*.exe|All files|*.*";
                    var current = ClientLauncher.ResolveOfficialLauncher(LauncherPath);
                    var directory = Path.GetDirectoryName(current);
                    if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
                    if (dialog.ShowDialog(form) != DialogResult.OK) return;
                    LauncherPath = dialog.FileName;
                    _pathBox.Text = LauncherPath;
                    SavePath();
                    RefreshStatus();
                }
            };
            card.Controls.Add(browse);

            var detect = MakeButton("AUTO DETECT", new Point(116, 136), 105);
            detect.Click += delegate
            {
                LauncherPath = ClientLauncher.DefaultOfficialLauncherPath;
                _pathBox.Text = LauncherPath;
                SavePath();
                RefreshStatus();
            };
            card.Controls.Add(detect);

            var test = MakeButton("TEST", new Point(229, 136), 89);
            test.Click += delegate { RefreshStatus(); };
            card.Controls.Add(test);

            _status = new Label
            {
                AutoSize = false,
                Location = new Point(18, 181),
                Size = new Size(300, 45),
                Font = new Font("Segoe UI", 8.7F)
            };
            card.Controls.Add(_status);

            settingsPage.Controls.Add(card);
            card.BringToFront();
            RefreshStatus();
        }

        public static string GetLauncherPath()
        {
            if (!string.IsNullOrWhiteSpace(LauncherPath)) return LauncherPath;
            return ClientLauncher.DefaultOfficialLauncherPath;
        }

        private static Button MakeButton(string text, Point location, int width)
        {
            var button = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(width, 29),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(26, 31, 38),
                ForeColor = Color.FromArgb(232, 235, 239),
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(45, 51, 60);
            button.FlatAppearance.BorderSize = 1;
            return button;
        }

        private static Panel FindSettingsPage(Control root)
        {
            foreach (Control child in root.Controls)
            {
                var panel = child as Panel;
                if (panel != null && ContainsSettingsTitle(panel)) return panel;
                var nested = FindSettingsPage(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private static bool ContainsSettingsTitle(Control root)
        {
            foreach (Control child in root.Controls)
            {
                var label = child as Label;
                if (label != null && string.Equals(label.Text, "SERVER SETTINGS", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string LoadPath()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var value = File.ReadAllText(SettingsPath).Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
            catch { }
            return ClientLauncher.DefaultOfficialLauncherPath;
        }

        private static void SavePath()
        {
            try { File.WriteAllText(SettingsPath, LauncherPath ?? string.Empty); }
            catch { }
        }

        private static void RefreshStatus()
        {
            if (_status == null) return;
            var validation = ClientLauncher.ValidateOfficialLauncher(GetLauncherPath());
            if (validation == null)
            {
                _status.Text = "READY — official launch chain will be used by GAME START.";
                _status.ForeColor = Color.FromArgb(51, 204, 119);
            }
            else
            {
                _status.Text = validation;
                _status.ForeColor = Color.FromArgb(236, 180, 71);
            }
        }
    }
}
