using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class VShopManagerEnhancement
    {
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

            var field = typeof(MainForm).GetField("_logContent", BindingFlags.Instance | BindingFlags.NonPublic);
            var logContent = field == null ? null : field.GetValue(form) as Panel;
            if (logContent == null) return;

            var settingsPage = FindSettingsPage(logContent);
            if (settingsPage == null) return;

            // Move the existing Log Behavior card down to make room for the VShop card.
            foreach (Control control in settingsPage.Controls)
            {
                var panel = control as Panel;
                if (panel != null && ContainsLabel(panel, "LOG BEHAVIOR"))
                    panel.Location = new Point(panel.Left, 876);
            }

            var status = new Label
            {
                AutoSize = false,
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 190),
                Size = new Size(650, 44)
            };

            var card = new Panel
            {
                BackColor = PanelColor,
                Location = new Point(24, 608),
                Size = new Size(700, 245),
                Padding = new Padding(20)
            };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            card.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "VISUAL SHOP / VSHOP CATALOG",
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Location = new Point(20, 18)
            });
            card.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Required client files: VShopItem.xlt and VisualItem.xlt. The importer creates/migrates dbo.visual_item_catalog and imports readable names, categories, real visual category indexes, prices for every period and visual stat bonuses.",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 50),
                Size = new Size(650, 68)
            });
            card.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Copy both files into the Importer folder next to DCServerManager.exe. Stop Game Server before importing.",
                ForeColor = WarningColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 116),
                Size = new Size(650, 32)
            });

            var importButton = MakeActionButton("IMPORT VSHOP XLT", new Point(20, 150), 200);
            var openFolderButton = MakeActionButton("OPEN IMPORTER FOLDER", new Point(230, 150), 220);
            card.Controls.Add(importButton);
            card.Controls.Add(openFolderButton);
            card.Controls.Add(status);
            settingsPage.Controls.Add(card);

            openFolderButton.Click += delegate
            {
                try
                {
                    var folder = VShopDataImporter.EnsureImportDirectory();
                    Process.Start("explorer.exe", folder);
                    UpdateStatus(status);
                }
                catch (Exception ex)
                {
                    SetStatus(status, "Could not open Importer folder: " + ex.Message, StoppedColor);
                }
            };

            importButton.Click += async delegate
            {
                if (!CanImportOffline(status)) return;

                var missing = VShopDataImporter.GetMissingRequiredFiles();
                if (missing.Length != 0)
                {
                    SetStatus(status, "Missing: " + string.Join(", ", missing) + ". Folder: " + VShopDataImporter.ImportFolder, WarningColor);
                    return;
                }

                importButton.Enabled = false;
                openFolderButton.Enabled = false;
                SetStatus(status, "Importing VShop XLT data into DCServer...", WarningColor);

                try
                {
                    var result = await Task.Run(delegate { return VShopDataImporter.ImportAll(); });
                    SetStatus(status,
                        "Imported " + result.Rows + " VShop rows. VisualItem matches: " + result.VisualMatches +
                        "; without match: " + result.MissingVisualMatches + ".",
                        RunningColor);
                }
                catch (Exception ex)
                {
                    SetStatus(status, "VShop import failed: " + ex.Message, StoppedColor);
                }
                finally
                {
                    importButton.Enabled = true;
                    openFolderButton.Enabled = true;
                }
            };

            UpdateStatus(status);
        }

        private static Panel FindSettingsPage(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                var panel = control as Panel;
                if (panel != null && ContainsLabel(panel, "SERVER SETTINGS")) return panel;
            }
            return null;
        }

        private static bool ContainsLabel(Control parent, string text)
        {
            foreach (Control control in parent.Controls)
            {
                var label = control as Label;
                if (label != null && string.Equals(label.Text, text, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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
                    SetStatus(status, "Stop Game Server before importing VShop data.", StoppedColor);
                    return false;
                }
                return true;
            }
            finally
            {
                if (processes != null) foreach (var process in processes) process.Dispose();
            }
        }

        private static void UpdateStatus(Label status)
        {
            var missing = VShopDataImporter.GetMissingRequiredFiles();
            if (missing.Length == 0)
                SetStatus(status, "VShopItem.xlt and VisualItem.xlt are ready to import.", RunningColor);
            else
                SetStatus(status, "Waiting for: " + string.Join(", ", missing), WarningColor);
        }

        private static void SetStatus(Label label, string text, Color color)
        {
            label.Text = text;
            label.ForeColor = color;
        }
    }
}
