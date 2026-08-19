using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ServerManager
{
    /// <summary>
    /// Extra manager UI kept separate from MainForm. Provides the Settings page
    /// and a robust repaint workaround for hidden RichTextBox log pages.
    /// </summary>
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
                if (button == null || button == settingsButton) continue;
                button.Click += serverTabRefresh;
            }

            tabStrip.ControlAdded += delegate(object sender, ControlEventArgs args)
            {
                var button = args.Control as Button;
                if (button == null || button == settingsButton) return;
                button.Click += serverTabRefresh;
            };

            settingsButton.Click += delegate
            {
                foreach (Control control in logContent.Controls)
                    control.Visible = false;

                foreach (Control control in tabStrip.Controls)
                {
                    var button = control as Button;
                    if (button != null) StyleTab(button, button == settingsButton);
                }

                settingsPage.Visible = true;
                settingsPage.BringToFront();
                settingsPage.PerformLayout();
                settingsPage.Invalidate(true);
                settingsPage.Update();
            };
        }

        private static Button BuildSettingsTabButton()
        {
            var button = new Button
            {
                Text = "Settings",
                Width = 130,
                Height = 37,
                Margin = new Padding(0),
                FlatStyle = FlatStyle.Flat,
                BackColor = SurfaceColor,
                ForeColor = MutedColor,
                Font = new Font("Segoe UI Semibold", 9F),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
                TabStop = false
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
                Padding = new Padding(24)
            };

            var title = new Label
            {
                AutoSize = true,
                Text = "SERVER SETTINGS",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
                Location = new Point(24, 22)
            };
            page.Controls.Add(title);

            var subtitle = new Label
            {
                AutoSize = true,
                Text = "Administrative tools and offline database synchronization",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9.5F),
                Location = new Point(27, 56)
            };
            page.Controls.Add(subtitle);

            var card = new Panel
            {
                BackColor = PanelColor,
                Location = new Point(24, 92),
                Size = new Size(690, 238),
                Padding = new Padding(20)
            };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            page.Controls.Add(card);

            var cardTitle = new Label
            {
                AutoSize = true,
                Text = "ITEM CATALOG DATABASE",
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Location = new Point(20, 18)
            };
            card.Controls.Add(cardTitle);

            var description = new Label
            {
                AutoSize = false,
                Text = "The Game Server generates Logs\\Catalogs\\ItemCatalog.json from Items.xml + UseItems.xml. The database import is intentionally OFFLINE: stop Game Server, then import the saved JSON into dbo.item_catalog.",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 50),
                Size = new Size(645, 55)
            };
            card.Controls.Add(description);

            var jsonLabel = new Label
            {
                AutoSize = false,
                Text = "JSON: " + ShortPath(ItemCatalogImporter.CatalogPath),
                ForeColor = MutedColor,
                Font = new Font("Consolas", 8.5F),
                Location = new Point(20, 105),
                Size = new Size(645, 22)
            };
            card.Controls.Add(jsonLabel);

            var importButton = new Button
            {
                Text = "IMPORT ITEMS TO DB",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(24, 46, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                Location = new Point(20, 137),
                Size = new Size(176, 38),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            importButton.FlatAppearance.BorderColor = Color.FromArgb(58, 93, 72);
            importButton.FlatAppearance.BorderSize = 1;
            importButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 59, 43);
            card.Controls.Add(importButton);

            var status = new Label
            {
                AutoSize = false,
                Text = GetInitialImportStatus(),
                ForeColor = GetInitialImportStatusColor(),
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 190),
                Size = new Size(645, 34)
            };
            card.Controls.Add(status);

            importButton.Click += delegate
            {
                if (IsGameServerRunning())
                {
                    status.ForeColor = StoppedColor;
                    status.Text = "Stop Game Server before importing. Offline import is blocked while GameServer.exe is running.";
                    return;
                }

                if (!ItemCatalogImporter.CatalogExists())
                {
                    status.ForeColor = StoppedColor;
                    status.Text = "ItemCatalog.json not found. Start Game Server once to generate it, then STOP Game Server and import.";
                    return;
                }

                importButton.Enabled = false;
                var oldCursor = form.Cursor;
                form.Cursor = Cursors.WaitCursor;
                status.ForeColor = WarningColor;
                status.Text = "Importing ItemCatalog.json into dbo.item_catalog...";
                status.Refresh();

                try
                {
                    var result = ItemCatalogImporter.Import();
                    status.ForeColor = RunningColor;
                    status.Text = "Import complete: " + result.Count + " item definitions synchronized to dbo.item_catalog.";
                }
                catch (Exception ex)
                {
                    status.ForeColor = StoppedColor;
                    status.Text = "Import failed: " + ex.Message;
                    MessageBox.Show(form, ex.ToString(), "Item catalog import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    form.Cursor = oldCursor;
                    importButton.Enabled = true;
                }
            };

            var steps = new Label
            {
                AutoSize = false,
                Text = "Workflow:  1) Start Game Server to generate/update JSON   2) Stop Game Server   3) Open Settings   4) Import Items to DB   5) Refresh Tables in SSMS",
                ForeColor = WarningColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(24, 352),
                Size = new Size(900, 48)
            };
            page.Controls.Add(steps);

            return page;
        }

        private static string GetInitialImportStatus()
        {
            if (IsGameServerRunning())
                return "Game Server is running. Stop it before importing the JSON catalog.";
            if (!ItemCatalogImporter.CatalogExists())
                return "ItemCatalog.json is missing. Start Game Server once to generate it.";
            return "Ready for offline import. ItemCatalog.json exists and Game Server is stopped.";
        }

        private static Color GetInitialImportStatusColor()
        {
            if (IsGameServerRunning()) return StoppedColor;
            return ItemCatalogImporter.CatalogExists() ? RunningColor : WarningColor;
        }

        private static bool IsGameServerRunning()
        {
            var processes = Process.GetProcessesByName("GameServer");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return "." + path.Substring(baseDir.Length);
            return path;
        }

        private static void QueueLogRefresh(MainForm form, Panel logContent)
        {
            try
            {
                form.BeginInvoke(new Action(delegate
                {
                    if (form.IsDisposed) return;

                    logContent.SuspendLayout();
                    logContent.ResumeLayout(true);
                    logContent.PerformLayout();
                    logContent.Invalidate(true);
                    logContent.Update();

                    foreach (var box in FindControls<RichTextBox>(logContent))
                    {
                        if (!box.Visible) continue;

                        // WinForms RichTextBox may keep a stale hidden backing surface.
                        // Force the visible page through a complete layout + redraw cycle.
                        var parent = box.Parent;
                        if (parent != null)
                        {
                            parent.PerformLayout();
                            parent.Invalidate(true);
                            parent.Update();
                        }

                        box.SuspendLayout();
                        box.ResumeLayout(true);
                        box.Invalidate(true);
                        box.Update();
                        box.Refresh();

                        if (box.TextLength > 0)
                        {
                            box.SelectionStart = box.TextLength;
                            box.SelectionLength = 0;
                            box.ScrollToCaret();
                        }

                        // A second queued repaint is intentional: it runs after the
                        // custom page Visibility/BringToFront window messages settle.
                        box.BeginInvoke(new Action(delegate
                        {
                            if (box.IsDisposed || !box.Visible) return;
                            box.Invalidate(true);
                            box.Update();
                            box.Refresh();
                        }));
                    }
                }));
            }
            catch
            {
                // Form may be shutting down.
            }
        }

        private static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                var match = child as T;
                if (match != null) yield return match;

                foreach (var nested in FindControls<T>(child))
                    yield return nested;
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
            button.Update();
        }
    }
}
