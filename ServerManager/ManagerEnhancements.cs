using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class ManagerEnhancements
    {
        private static readonly Color BackgroundColor = Color.FromArgb(9, 11, 14);
        private static readonly Color SurfaceColor = Color.FromArgb(14, 17, 22);
        private static readonly Color PanelColor = Color.FromArgb(20, 24, 30);
        private static readonly Color BorderColor = Color.FromArgb(45, 51, 60);
        private static readonly Color TextColor = Color.FromArgb(232, 235, 239);
        private static readonly Color MutedColor = Color.FromArgb(133, 142, 155);
        private static readonly Color RunningColor = Color.FromArgb(51, 204, 119);
        private static readonly Color WarningColor = Color.FromArgb(236, 180, 71);
        private static readonly Color StoppedColor = Color.FromArgb(238, 82, 83);
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerManager.settings");

        public static bool ClearVisibleLogsOnServerStart { get; private set; }

        public static void Attach(MainForm form)
        {
            if (form == null) return;
            LoadSettings();

            var tabStrip = GetPrivateField<FlowLayoutPanel>(form, "_tabStrip");
            var logContent = GetPrivateField<Panel>(form, "_logContent");
            if (tabStrip == null || logContent == null) return;

            var settingsPage = BuildSettingsPage(form);
            settingsPage.Visible = false;
            logContent.Controls.Add(settingsPage);

            var settingsButton = BuildSettingsTabButton();
            tabStrip.Controls.Add(settingsButton);

            EventHandler serverTabRefresh = delegate
            {
                settingsPage.Visible = false;
                StyleTab(settingsButton, false);
            };

            foreach (Control control in tabStrip.Controls)
            {
                var button = control as Button;
                if (button != null && button != settingsButton) button.Click += serverTabRefresh;
            }

            tabStrip.ControlAdded += delegate(object sender, ControlEventArgs args)
            {
                var button = args.Control as Button;
                if (button != null && button != settingsButton) button.Click += serverTabRefresh;
            };

            settingsButton.Click += delegate
            {
                foreach (Control control in logContent.Controls) control.Visible = false;
                foreach (Control control in tabStrip.Controls)
                {
                    var button = control as Button;
                    if (button != null) StyleTab(button, button == settingsButton);
                }
                settingsPage.Visible = true;
                settingsPage.BringToFront();
                settingsPage.Invalidate(true);
            };
        }

        private static Button BuildSettingsTabButton()
        {
            var button = new Button
            {
                Text = "Settings", Width = 130, Height = 37, Margin = new Padding(0),
                FlatStyle = FlatStyle.Flat, BackColor = SurfaceColor, ForeColor = MutedColor,
                Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand, TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(25, 29, 36);
            return button;
        }

        private static Panel BuildSettingsPage(MainForm form)
        {
            var page = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BackgroundColor,
                Padding = new Padding(24),
                AutoScroll = true
            };

            page.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "SERVER SETTINGS",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
                Location = new Point(24, 22)
            });
            page.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Global client-data import, logging and administrative synchronization",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9.5F),
                Location = new Point(27, 56)
            });

            var status = new Label
            {
                AutoSize = false,
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 170),
                Size = new Size(650, 42)
            };

            var importCard = new Panel
            {
                BackColor = PanelColor,
                Location = new Point(24, 92),
                Size = new Size(700, 225),
                Padding = new Padding(20)
            };
            importCard.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(pen, 0, 0, importCard.Width - 1, importCard.Height - 1);
            };

            importCard.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "CLIENT DATA IMPORT",
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Location = new Point(20, 18)
            });
            importCard.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Place every client .tdf file inside the Improter folder next to DCServerManager.exe. " +
                       "Each TDF is imported into its own dbo.client_* table with RowIndex, ClientTableIndex where applicable, and one SQL column per original TDF column.",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 50),
                Size = new Size(650, 62)
            });

            var importButton = MakeActionButton("IMPORT ALL CLIENT DATA", new Point(20, 120), 215);
            var openFolderButton = MakeActionButton("OPEN IMPROTER FOLDER", new Point(245, 120), 205);
            importCard.Controls.Add(importButton);
            importCard.Controls.Add(openFolderButton);
            importCard.Controls.Add(status);
            page.Controls.Add(importCard);

            openFolderButton.Click += delegate
            {
                try
                {
                    var folder = ClientDataImporter.EnsureImportDirectory();
                    Process.Start("explorer.exe", folder);
                }
                catch (Exception ex)
                {
                    SetStatus(status, "Could not open Improter folder: " + ex.Message, StoppedColor);
                }
            };

            importButton.Click += async delegate
            {
                if (!CanImportOffline(status)) return;

                var count = ClientDataImporter.CountTdfFiles();
                if (count == 0)
                {
                    SetStatus(status, "No .tdf files found. Put the client files in: " + ClientDataImporter.ImportFolder, WarningColor);
                    return;
                }

                importButton.Enabled = false;
                openFolderButton.Enabled = false;
                SetStatus(status, "Importing " + count + " TDF files in background...", WarningColor);

                try
                {
                    var result = await Task.Run(delegate { return ClientDataImporter.ImportAll(); });
                    SetStatus(status,
                        "Imported " + result.Files + " TDF tables and " + result.Rows +
                        " rows. Item lookup rows: " + result.ItemLookupRows + ".",
                        RunningColor);
                }
                catch (Exception ex)
                {
                    SetStatus(status, "Import failed: " + ex.Message, StoppedColor);
                }
                finally
                {
                    importButton.Enabled = true;
                    openFolderButton.Enabled = true;
                }
            };

            UpdateClientImportStatus(status);

            var logCard = new Panel
            {
                BackColor = PanelColor,
                Location = new Point(24, 340),
                Size = new Size(700, 130),
                Padding = new Padding(20)
            };
            logCard.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor)) e.Graphics.DrawRectangle(pen, 0, 0, logCard.Width - 1, logCard.Height - 1);
            };
            logCard.Controls.Add(new Label
            {
                AutoSize = true, Text = "LOG BEHAVIOR", ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold), Location = new Point(20, 18)
            });
            logCard.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Clear the visible log panel for a server whenever that server starts. Structured packet/session files under Logs\\ are never deleted by this option.",
                ForeColor = MutedColor, Font = new Font("Segoe UI", 9F), Location = new Point(20, 48), Size = new Size(650, 38)
            });
            var clearLogs = new CheckBox
            {
                AutoSize = true,
                Text = "Clear visible logs on server start",
                Checked = ClearVisibleLogsOnServerStart,
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 9F),
                Location = new Point(20, 94),
                Cursor = Cursors.Hand
            };
            clearLogs.CheckedChanged += delegate
            {
                ClearVisibleLogsOnServerStart = clearLogs.Checked;
                SaveSettings();
            };
            logCard.Controls.Add(clearLogs);
            page.Controls.Add(logCard);

            return page;
        }

        private static Button MakeActionButton(string text, Point location, int width)
        {
            var button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(24, 46, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                Location = location,
                Size = new Size(width, 38),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(58, 93, 72);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 59, 43);
            return button;
        }

        private static bool CanImportOffline(Label status)
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("GameServer");
                if (processes.Length > 0)
                {
                    SetStatus(status, "Stop Game Server before importing client data.", StoppedColor);
                    return false;
                }
                return true;
            }
            finally
            {
                if (processes != null) foreach (var process in processes) process.Dispose();
            }
        }

        private static void UpdateClientImportStatus(Label status)
        {
            var count = ClientDataImporter.CountTdfFiles();
            if (count > 0)
                SetStatus(status, count + " TDF files found in Improter. Stop Game Server before importing.", RunningColor);
            else
                SetStatus(status, "Improter folder ready. Copy the client .tdf files there.", WarningColor);
        }

        private static void SetStatus(Label label, string text, Color color)
        {
            label.Text = text;
            label.ForeColor = color;
        }

        private static void LoadSettings()
        {
            ClearVisibleLogsOnServerStart = false;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    if (parts[0].Trim().Equals("ClearVisibleLogsOnServerStart", StringComparison.OrdinalIgnoreCase))
                    {
                        bool value;
                        if (bool.TryParse(parts[1].Trim(), out value)) ClearVisibleLogsOnServerStart = value;
                    }
                }
            }
            catch { }
        }

        private static void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath,
                    "ClearVisibleLogsOnServerStart=" + ClearVisibleLogsOnServerStart + Environment.NewLine);
            }
            catch { }
        }

        private static T GetPrivateField<T>(MainForm form, string name) where T : class
        {
            var field = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(form) as T;
        }

        private static void StyleTab(Button button, bool active)
        {
            if (button == null) return;
            button.BackColor = active ? PanelColor : SurfaceColor;
            button.ForeColor = active ? TextColor : MutedColor;
            button.Invalidate();
        }
    }
}
