using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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

        public static void Attach(MainForm form)
        {
            if (form == null) return;
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
                QueueLogRefresh(form, logContent);
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
                settingsPage.Update();
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
                AutoSize = true, Text = "Offline catalog imports and administrative database synchronization",
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
                Text = "Workflow: START Game Server once to regenerate JSON catalogs → STOP Game Server → import from Settings. Imports are intentionally blocked while GameServer.exe is running.",
                ForeColor = WarningColor, Font = new Font("Segoe UI", 9F), Location = new Point(24, 535), Size = new Size(850, 45)
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

        private static void QueueLogRefresh(MainForm form, Panel logContent)
        {
            try
            {
                form.BeginInvoke(new Action(delegate
                {
                    if (form.IsDisposed) return;
                    logContent.Invalidate(true); logContent.Update();
                    foreach (var box in FindControls<RichTextBox>(logContent))
                    {
                        if (!box.Visible) continue;
                        box.Visible = false;
                        box.Visible = true;
                        box.Invalidate(true); box.Update(); box.Refresh();
                        if (box.TextLength > 0)
                        {
                            box.SelectionStart = box.TextLength;
                            box.SelectionLength = 0;
                            box.ScrollToCaret();
                        }
                    }
                    form.BeginInvoke(new Action(delegate
                    {
                        foreach (var box in FindControls<RichTextBox>(logContent))
                        {
                            if (!box.Visible) continue;
                            box.Invalidate(true); box.Refresh();
                        }
                    }));
                }));
            }
            catch { }
        }

        private static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                var match = child as T;
                if (match != null) yield return match;
                foreach (var nested in FindControls<T>(child)) yield return nested;
            }
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
