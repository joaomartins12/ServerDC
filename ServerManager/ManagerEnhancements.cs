using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
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

            // MainForm.SelectTab already performs the complete server-log page switch.
            // Previously every tab click also forced a second refresh that invalidated the whole
            // log tree, hid/showed RichTextBoxes, called Update/Refresh repeatedly and queued an
            // additional BeginInvoke. With large packet logs that work could monopolize the UI
            // thread for noticeable periods. Keep this handler intentionally lightweight.
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
                // Invalidate is enough. Do not synchronously force Update(); the normal WinForms
                // paint cycle keeps the UI responsive when large logs are being appended.
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
            var page = new Panel { Dock = DockStyle.Fill, BackColor = BackgroundColor, Padding = new Padding(24), AutoScroll = true };

            page.Controls.Add(new Label
            {
                AutoSize = true, Text = "SERVER SETTINGS", ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold), Location = new Point(24, 22)
            });
            page.Controls.Add(new Label
            {
                AutoSize = true, Text = "Offline catalog imports, logging and administrative database synchronization",
                ForeColor = MutedColor, Font = new Font("Segoe UI", 9.5F), Location = new Point(27, 56)
            });

            var itemStatus = new Label();
            var itemCard = BuildCatalogCard(
                "ITEM CATALOG DATABASE",
                "Import the generated ItemCatalog.json into dbo.item_catalog. The Game Server must be stopped. Existing administrative price/enable overrides are preserved.",
                "IMPORT ITEMS TO DB", new Point(24, 92), itemStatus);
            page.Controls.Add(itemCard);

            var vehicleStatus = new Label();
            var vehicleCard = BuildCatalogCard(
                "VEHICLE CATALOG DATABASE",
                "Import VehicleCatalog.json into dbo.vehicle_catalog and dbo.vehicle_upgrade_catalog, including real base stats and every V1-V9 upgrade definition.",
                "IMPORT VEHICLES TO DB", new Point(24, 310), vehicleStatus);
            page.Controls.Add(vehicleCard);

            var logCard = new Panel { BackColor = PanelColor, Location = new Point(24, 528), Size = new Size(700, 130), Padding = new Padding(20) };
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

            var itemButton = FindButton(itemCard);
            itemButton.Click += delegate
            {
                if (!CanImportOffline(itemStatus)) return;
                if (!ItemCatalogImporter.CatalogExists())
                {
                    SetStatus(itemStatus, "ItemCatalog.json not found. Start Game Server once to generate it, then STOP it and import.", StoppedColor);
                    return;
                }
                try
                {
                    SetStatus(itemStatus, "Importing ItemCatalog.json...", WarningColor);
                    Application.DoEvents();
                    var result = ItemCatalogImporter.Import();
                    SetStatus(itemStatus, "Imported " + result.Count + " item definitions successfully.", RunningColor);
                }
                catch (Exception ex)
                {
                    SetStatus(itemStatus, "Import failed: " + ex.Message, StoppedColor);
                }
            };

            var vehicleButton = FindButton(vehicleCard);
            vehicleButton.Click += delegate
            {
                if (!CanImportOffline(vehicleStatus)) return;
                if (!VehicleCatalogImporter.CatalogExists())
                {
                    SetStatus(vehicleStatus, "VehicleCatalog.json not found. Start Game Server once to generate it, then STOP it and import.", StoppedColor);
                    return;
                }
                try
                {
                    SetStatus(vehicleStatus, "Importing VehicleCatalog.json...", WarningColor);
                    Application.DoEvents();
                    var result = VehicleCatalogImporter.Import();
                    SetStatus(vehicleStatus, "Imported " + result.Vehicles + " vehicles and " + result.Upgrades + " upgrade rows.", RunningColor);
                }
                catch (Exception ex)
                {
                    SetStatus(vehicleStatus, "Import failed: " + ex.Message, StoppedColor);
                }
            };

            page.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Catalog workflow: START Game Server once to regenerate JSON catalogs → STOP Game Server → import from Settings. Imports are blocked while GameServer.exe is running.",
                ForeColor = WarningColor, Font = new Font("Segoe UI", 9F), Location = new Point(24, 680), Size = new Size(850, 45)
            });

            UpdateCatalogStatus(itemStatus, ItemCatalogImporter.CatalogExists(), "ItemCatalog.json");
            UpdateCatalogStatus(vehicleStatus, VehicleCatalogImporter.CatalogExists(), "VehicleCatalog.json");
            return page;
        }

        private static Panel BuildCatalogCard(string title, string description, string buttonText, Point location, Label status)
        {
            var card = new Panel { BackColor = PanelColor, Location = location, Size = new Size(700, 195), Padding = new Padding(20) };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            card.Controls.Add(new Label
            {
                AutoSize = true, Text = title, ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold), Location = new Point(20, 18)
            });
            card.Controls.Add(new Label
            {
                AutoSize = false, Text = description, ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F), Location = new Point(20, 50), Size = new Size(650, 52)
            });
            var button = new Button
            {
                Text = buttonText, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(24, 46, 34),
                ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                Location = new Point(20, 112), Size = new Size(195, 38), Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(58, 93, 72);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 59, 43);
            card.Controls.Add(button);

            status.AutoSize = true;
            status.ForeColor = MutedColor;
            status.Font = new Font("Segoe UI", 9F);
            status.Location = new Point(20, 162);
            card.Controls.Add(status);
            return card;
        }

        private static Button FindButton(Control root)
        {
            foreach (Control control in root.Controls)
            {
                var button = control as Button;
                if (button != null) return button;
            }
            return null;
        }

        private static bool CanImportOffline(Label status)
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("GameServer");
                if (processes.Length > 0)
                {
                    SetStatus(status, "Stop Game Server before importing. Offline import is blocked while GameServer.exe is running.", StoppedColor);
                    return false;
                }
                return true;
            }
            finally
            {
                if (processes != null) foreach (var process in processes) process.Dispose();
            }
        }

        private static void UpdateCatalogStatus(Label status, bool exists, string name)
        {
            if (exists) SetStatus(status, name + " found. Stop Game Server before importing.", RunningColor);
            else SetStatus(status, name + " not found. Start Game Server once to generate it.", WarningColor);
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
