using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class VehicleKeySettingsExtension
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

            var logContent = GetPrivateField<Panel>(form, "_logContent");
            if (logContent == null) return;

            var settingsPage = logContent.Controls
                .OfType<Panel>()
                .FirstOrDefault(IsSettingsPage);
            if (settingsPage == null) return;

            // Make room between the Vehicle Catalog card and Log Behavior card.
            foreach (Control control in settingsPage.Controls)
            {
                var panel = control as Panel;
                if (panel != null && ContainsLabel(panel, "LOG BEHAVIOR"))
                    panel.Top = 746;

                var label = control as Label;
                if (label != null && label.Text != null && label.Text.StartsWith("Catalog workflow:", StringComparison.OrdinalIgnoreCase))
                    label.Top = 900;
            }

            var status = new Label
            {
                AutoSize = false,
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 158),
                Size = new Size(650, 28)
            };

            var card = new Panel
            {
                BackColor = PanelColor,
                Location = new Point(24, 528),
                Size = new Size(700, 195),
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
                Text = "VEHICLE KEY MAP",
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Location = new Point(20, 18)
            });
            card.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Import VehicleKeyMap.tsv into dbo.vehicle_catalog.KeyItemId. Only vehicles already present in the database are updated; no vehicle rows are created.",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 50),
                Size = new Size(650, 52)
            });

            var import = new Button
            {
                Text = "IMPORT VEHICLE KEYS",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(24, 46, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                Location = new Point(20, 112),
                Size = new Size(195, 38),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            import.FlatAppearance.BorderColor = Color.FromArgb(58, 93, 72);
            import.FlatAppearance.BorderSize = 1;
            import.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 59, 43);

            import.Click += delegate
            {
                if (!CanImportOffline(status)) return;

                var defaultPath = VehicleKeyMapImporter.DefaultPath;
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "Select VehicleKeyMap.tsv";
                    dialog.Filter = "Vehicle key map (*.tsv)|*.tsv|All files (*.*)|*.*";
                    dialog.CheckFileExists = true;
                    dialog.Multiselect = false;
                    if (File.Exists(defaultPath))
                    {
                        dialog.InitialDirectory = Path.GetDirectoryName(defaultPath);
                        dialog.FileName = Path.GetFileName(defaultPath);
                    }

                    if (dialog.ShowDialog(form) != DialogResult.OK)
                        return;

                    try
                    {
                        status.Text = "Importing vehicle key map...";
                        status.ForeColor = WarningColor;
                        Application.DoEvents();

                        var result = VehicleKeyMapImporter.Import(dialog.FileName);
                        status.Text = "Updated " + result.Updated + ", already correct " + result.AlreadyCorrect +
                                      ", unmatched " + result.Unmatched + " of " + result.ExistingVehicles + " DB vehicles.";
                        status.ForeColor = RunningColor;
                    }
                    catch (Exception ex)
                    {
                        status.Text = "Import failed: " + ex.Message;
                        status.ForeColor = StoppedColor;
                    }
                }
            };

            status.Text = File.Exists(VehicleKeyMapImporter.DefaultPath)
                ? "VehicleKeyMap.tsv found. Stop Game Server and import."
                : "Choose the VehicleKeyMap.tsv file to import.";
            status.ForeColor = File.Exists(VehicleKeyMapImporter.DefaultPath) ? RunningColor : WarningColor;

            card.Controls.Add(import);
            card.Controls.Add(status);
            settingsPage.Controls.Add(card);
            card.BringToFront();
        }

        private static bool IsSettingsPage(Panel panel)
        {
            return panel.Controls.OfType<Label>().Any(x => string.Equals(x.Text, "SERVER SETTINGS", StringComparison.Ordinal));
        }

        private static bool ContainsLabel(Control root, string text)
        {
            return root.Controls.OfType<Label>().Any(x => string.Equals(x.Text, text, StringComparison.Ordinal));
        }

        private static bool CanImportOffline(Label status)
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("GameServer");
                if (processes.Length == 0) return true;
                status.Text = "Stop Game Server before importing vehicle keys.";
                status.ForeColor = StoppedColor;
                return false;
            }
            finally
            {
                if (processes != null)
                    foreach (var process in processes) process.Dispose();
            }
        }

        private static T GetPrivateField<T>(MainForm form, string name) where T : class
        {
            var field = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(form) as T;
        }
    }
}
