using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerManager
{
    /// <summary>
    /// Adds the box/reward database tools to the existing Settings page without
    /// coupling them to the legacy ServerManager.settings file. The schema option
    /// is intentionally idempotent: when enabled, every ServerManager startup
    /// verifies the tables and creates only what is missing.
    /// </summary>
    internal static class RewardManagerEnhancement
    {
        private static readonly Color PanelColor = Color.FromArgb(20, 24, 30);
        private static readonly Color BorderColor = Color.FromArgb(45, 51, 60);
        private static readonly Color TextColor = Color.FromArgb(232, 235, 239);
        private static readonly Color MutedColor = Color.FromArgb(133, 142, 155);
        private static readonly Color RunningColor = Color.FromArgb(51, 204, 119);
        private static readonly Color WarningColor = Color.FromArgb(236, 180, 71);
        private static readonly Color StoppedColor = Color.FromArgb(238, 82, 83);

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "RewardFeatures.settings");

        public static bool AutoCreateRewardSchemaOnStartup { get; private set; } = true;

        public static void Attach(MainForm form)
        {
            if (form == null) return;
            LoadSettings();

            var logContent = GetPrivateField<Panel>(form, "_logContent");
            if (logContent == null) return;

            var settingsPage = FindSettingsPage(logContent);
            if (settingsPage == null) return;

            var status = new Label
            {
                AutoSize = false,
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 218),
                Size = new Size(650, 48)
            };

            var card = new Panel
            {
                BackColor = PanelColor,
                Location = new Point(24, 760),
                Size = new Size(700, 285),
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
                Text = "BOX / REWARD DATABASE",
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Location = new Point(20, 18)
            });

            card.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Creates the SQL Server reward schema and imports RewardGroup.xlt, VisualItem.xlt and UseItemClient.tdf into normalized server-side tables. " +
                       "VShop box purchases and Lucky Bag rewards use these tables instead of trusting client data.",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 48),
                Size = new Size(650, 58)
            });

            var autoCreate = new CheckBox
            {
                AutoSize = true,
                Text = "Create / verify reward tables when ServerManager starts",
                Checked = AutoCreateRewardSchemaOnStartup,
                ForeColor = TextColor,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 9F),
                Location = new Point(20, 108),
                Cursor = Cursors.Hand
            };
            autoCreate.CheckedChanged += delegate
            {
                AutoCreateRewardSchemaOnStartup = autoCreate.Checked;
                SaveSettings();
                SetStatus(status,
                    autoCreate.Checked
                        ? "Automatic reward schema verification enabled."
                        : "Automatic reward schema verification disabled; manual schema button remains available.",
                    autoCreate.Checked ? RunningColor : WarningColor);
            };
            card.Controls.Add(autoCreate);

            var schemaButton = MakeActionButton("CREATE / VERIFY SQL TABLES", new Point(20, 142), 215);
            var importButton = MakeActionButton("IMPORT BOX / REWARD DATA", new Point(245, 142), 215);
            var folderButton = MakeActionButton("OPEN REWARD FOLDER", new Point(470, 142), 200);
            card.Controls.Add(schemaButton);
            card.Controls.Add(importButton);
            card.Controls.Add(folderButton);
            card.Controls.Add(status);
            settingsPage.Controls.Add(card);

            folderButton.Click += delegate
            {
                try
                {
                    var folder = RewardDataImporter.EnsureImportDirectory();
                    Process.Start("explorer.exe", folder);
                }
                catch (Exception ex)
                {
                    SetStatus(status, "Could not open RewardImporter folder: " + ex.Message, StoppedColor);
                }
            };

            schemaButton.Click += async delegate
            {
                schemaButton.Enabled = false;
                SetStatus(status, "Creating / verifying reward tables in DCServer...", WarningColor);
                try
                {
                    await Task.Run(delegate { RewardDataImporter.EnsureSchema(); });
                    UpdateImportStatus(status, "Reward SQL tables are ready.");
                }
                catch (Exception ex)
                {
                    SetStatus(status, "Reward schema failed: " + ex.Message, StoppedColor);
                }
                finally
                {
                    schemaButton.Enabled = true;
                }
            };

            importButton.Click += async delegate
            {
                if (!CanImportOffline(status)) return;

                var missing = RewardDataImporter.GetMissingRequiredFiles();
                if (missing.Length != 0)
                {
                    SetStatus(status,
                        "Missing: " + string.Join(", ", missing) + ". Folder: " + RewardDataImporter.ImportFolder,
                        WarningColor);
                    return;
                }

                schemaButton.Enabled = false;
                importButton.Enabled = false;
                folderButton.Enabled = false;
                SetStatus(status, "Importing box/reward client data into DCServer...", WarningColor);

                try
                {
                    var result = await Task.Run(delegate { return RewardDataImporter.ImportAll(); });
                    SetStatus(status,
                        "Imported " + result.SourceFiles + " source files / " + result.SourceRows +
                        " rows. UseItem maps: " + result.UseItemMappings +
                        ", rewards: " + result.RewardEntries +
                        ", VShop box maps: " + result.VisualBoxMappings + ".",
                        RunningColor);
                }
                catch (Exception ex)
                {
                    SetStatus(status, "Reward import failed: " + ex.Message, StoppedColor);
                }
                finally
                {
                    schemaButton.Enabled = true;
                    importButton.Enabled = true;
                    folderButton.Enabled = true;
                }
            };

            UpdateImportStatus(status, null);

            if (AutoCreateRewardSchemaOnStartup)
            {
                form.Shown += async delegate
                {
                    SetStatus(status, "Verifying reward SQL tables on startup...", WarningColor);
                    try
                    {
                        await Task.Run(delegate { RewardDataImporter.EnsureSchema(); });
                        UpdateImportStatus(status, "Reward SQL schema verified on startup.");
                    }
                    catch (Exception ex)
                    {
                        SetStatus(status, "Automatic reward schema verification failed: " + ex.Message, StoppedColor);
                    }
                };
            }
        }

        private static Panel FindSettingsPage(Panel logContent)
        {
            foreach (Control control in logContent.Controls)
            {
                var panel = control as Panel;
                if (panel == null) continue;
                if (ContainsLabel(panel, "SERVER SETTINGS")) return panel;
            }
            return null;
        }

        private static bool ContainsLabel(Control root, string text)
        {
            foreach (Control child in root.Controls)
            {
                var label = child as Label;
                if (label != null && string.Equals(label.Text, text, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (child.HasChildren && ContainsLabel(child, text)) return true;
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
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
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
                    SetStatus(status, "Stop Game Server before importing reward data.", StoppedColor);
                    return false;
                }
                return true;
            }
            finally
            {
                if (processes != null)
                    foreach (var process in processes) process.Dispose();
            }
        }

        private static void UpdateImportStatus(Label status, string prefix)
        {
            var missing = RewardDataImporter.GetMissingRequiredFiles();
            if (missing.Length == 0)
            {
                SetStatus(status,
                    (string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix + " ") +
                    "All reward source files are ready for manual import.",
                    RunningColor);
            }
            else
            {
                SetStatus(status,
                    (string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix + " ") +
                    "Waiting for: " + string.Join(", ", missing) + ".",
                    WarningColor);
            }
        }

        private static void SetStatus(Label label, string text, Color color)
        {
            if (label == null || label.IsDisposed) return;
            if (label.InvokeRequired)
            {
                label.BeginInvoke((MethodInvoker)delegate { SetStatus(label, text, color); });
                return;
            }
            label.Text = text;
            label.ForeColor = color;
        }

        private static void LoadSettings()
        {
            AutoCreateRewardSchemaOnStartup = true;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    if (!parts[0].Trim().Equals("AutoCreateRewardSchemaOnStartup", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool value;
                    if (bool.TryParse(parts[1].Trim(), out value))
                        AutoCreateRewardSchemaOnStartup = value;
                }
            }
            catch
            {
                AutoCreateRewardSchemaOnStartup = true;
            }
        }

        private static void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath,
                    "AutoCreateRewardSchemaOnStartup=" + AutoCreateRewardSchemaOnStartup + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static T GetPrivateField<T>(MainForm form, string name) where T : class
        {
            var field = typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(form) as T;
        }
    }
}
