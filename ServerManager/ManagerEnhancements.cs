using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ServerManager
{
    /// <summary>
    /// Extra manager UI that is kept separate from MainForm so the server process
    /// controller stays simple. Adds the Settings page and works around the WinForms
    /// RichTextBox repaint issue that can occur when switching custom log pages.
    /// </summary>
    internal static class ManagerEnhancements
    {
        private static readonly Color BackgroundColor = Color.FromArgb(9, 11, 14);
        private static readonly Color SurfaceColor = Color.FromArgb(14, 17, 22);
        private static readonly Color PanelColor = Color.FromArgb(20, 24, 30);
        private static readonly Color BorderColor = Color.FromArgb(45, 51, 60);
        private static readonly Color TextColor = Color.FromArgb(232, 235, 239);
        private static readonly Color MutedColor = Color.FromArgb(133, 142, 155);
        private static readonly Color AccentColor = Color.FromArgb(84, 132, 255);
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

            EventHandler serverTabRefresh = delegate(object sender, EventArgs args)
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

            // Covers any future dynamically-created server tabs too.
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
                Text = "Administrative tools and database synchronization",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9.5F),
                Location = new Point(27, 56)
            };
            page.Controls.Add(subtitle);

            var card = new Panel
            {
                BackColor = PanelColor,
                Location = new Point(24, 92),
                Size = new Size(620, 205),
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
                Text = "Reload Items.xml and UseItems.xml, regenerate ItemCatalog.json and synchronize all runtime TableIndex definitions to dbo.item_catalog. Administrative price/enable overrides are preserved.",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 50),
                Size = new Size(575, 52)
            };
            card.Controls.Add(description);

            var importButton = new Button
            {
                Text = "IMPORT ITEMS TO DB",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(24, 46, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                Location = new Point(20, 116),
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
                AutoSize = true,
                Text = "Ready. Game Server must be running from this Manager.",
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(20, 168)
            };
            card.Controls.Add(status);

            importButton.Click += delegate
            {
                string message;
                Color color;
                if (SendGameCommand(form, "importitems", out message))
                {
                    color = RunningColor;
                    status.Text = "Import requested. Follow progress in the Game Log.";
                }
                else
                {
                    color = StoppedColor;
                    status.Text = message;
                }
                status.ForeColor = color;
            };

            var hint = new Label
            {
                AutoSize = false,
                Text = "Tip: the import is intentionally executed by GameServer so XML parsing, TableIndex ordering and SQL synchronization use exactly the same code path as normal server startup.",
                ForeColor = WarningColor,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(24, 320),
                Size = new Size(700, 48)
            };
            page.Controls.Add(hint);

            return page;
        }

        private static bool SendGameCommand(MainForm form, string command, out string error)
        {
            error = null;
            try
            {
                var serversField = typeof(MainForm).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic);
                var servers = serversField == null ? null : serversField.GetValue(form) as IDictionary;
                if (servers == null || !servers.Contains("Game"))
                {
                    error = "Game Server entry was not found.";
                    return false;
                }

                var gameEntry = servers["Game"];
                if (gameEntry == null)
                {
                    error = "Game Server entry was not found.";
                    return false;
                }

                var processField = gameEntry.GetType().GetField("Process", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var process = processField == null ? null : processField.GetValue(gameEntry) as Process;
                if (process == null || process.HasExited)
                {
                    error = "Start Game Server from this Manager before importing.";
                    return false;
                }

                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex)
            {
                error = "Import command failed: " + ex.Message;
                return false;
            }
        }

        private static void QueueLogRefresh(MainForm form, Panel logContent)
        {
            try
            {
                form.BeginInvoke(new Action(delegate
                {
                    if (form.IsDisposed) return;

                    // A hidden RichTextBox can keep an old backing surface in WinForms.
                    // Recreating the visible paint immediately fixes the blank-until-scroll issue.
                    logContent.Invalidate(true);
                    logContent.Update();

                    foreach (var box in FindControls<RichTextBox>(logContent))
                    {
                        if (!box.Visible) continue;
                        box.Invalidate(true);
                        box.Update();
                        box.Refresh();

                        if (box.TextLength > 0)
                        {
                            box.SelectionStart = box.TextLength;
                            box.SelectionLength = 0;
                            box.ScrollToCaret();
                        }
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
        }
    }
}
