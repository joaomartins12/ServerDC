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

        private static TextBox _clientFolderBox;
        private static TextBox _clientExeBox;
        private static Label _clientStatus;
        private static Button _gameStartButton;

        public static bool ClearVisibleLogsOnServerStart { get; private set; }
        public static string ClientFolder { get; private set; }
        public static string ClientExecutable { get; private set; }
        public static bool RedirectOfficialClient { get; private set; }
        public static bool StartAllBeforeGame { get; private set; }
        public static bool RestoreRedirectOnClientExit { get; private set; }

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
                RefreshClientStatus();
            };

            AttachGameStartButton(form);
            form.FormClosing += delegate
            {
                if (RestoreRedirectOnClientExit)
                    ClientLauncher.DisableRedirect(delegate(string message) { Debug.WriteLine(message); });
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
                AutoSize = true, Text = "Client launcher, offline catalog imports, logging and administrative database synchronization",
                ForeColor = MutedColor, Font = new Font("Segoe UI", 9.5F), Location = new Point(27, 56)
            });

            var clientCard = BuildClientCard(form, new Point(24, 92));
            page.Controls.Add(clientCard);

            var itemStatus = new Label();
            var itemCard = BuildCatalogCard(
                "ITEM CATALOG DATABASE",
                "Import the generated ItemCatalog.json into dbo.item_catalog. The Game Server must be stopped. Existing administrative price/enable overrides are preserved.",
                "IMPORT ITEMS TO DB", new Point(24, 362), itemStatus);
            page.Controls.Add(itemCard);

            var vehicleStatus = new Label();
            var vehicleCard = BuildCatalogCard(
                "VEHICLE CATALOG DATABASE",
                "Import VehicleCatalog.json into dbo.vehicle_catalog and dbo.vehicle_upgrade_catalog, including real base stats and every V1-V9 upgrade definition.",
                "IMPORT VEHICLES TO DB", new Point(24, 580), vehicleStatus);
            page.Controls.Add(vehicleCard);

            var logCard = new Panel { BackColor = PanelColor, Location = new Point(24, 798), Size = new Size(700, 130), Padding = new Padding(20) };
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
                ForeColor = WarningColor, Font = new Font("Segoe UI", 9F), Location = new Point(24, 950), Size = new Size(850, 45)
            });

            UpdateCatalogStatus(itemStatus, ItemCatalogImporter.CatalogExists(), "ItemCatalog.json");
            UpdateCatalogStatus(vehicleStatus, VehicleCatalogImporter.CatalogExists(), "VehicleCatalog.json");
            RefreshClientStatus();
            return page;
        }

        private static Panel BuildClientCard(MainForm form, Point location)
        {
            var card = new Panel { BackColor = PanelColor, Location = location, Size = new Size(900, 245), Padding = new Padding(20) };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor)) e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            card.Controls.Add(new Label
            {
                AutoSize = true, Text = "GAME CLIENT", ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold), Location = new Point(20, 16)
            });
            card.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Official endpoint " + ClientLauncher.OfficialServerIp + "  →  local server. Redirect is temporary and does not modify skidrush.exe.",
                ForeColor = MutedColor, Font = new Font("Segoe UI", 9F), Location = new Point(20, 43)
            });

            _clientFolderBox = MakePathBox(ClientFolder, new Point(20, 73), 660);
            card.Controls.Add(_clientFolderBox);
            var browseFolder = MakeSmallButton("CLIENT FOLDER", new Point(692, 72), 170);
            browseFolder.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select the official Drift City / SkidRush client folder";
                    if (Directory.Exists(_clientFolderBox.Text)) dialog.SelectedPath = _clientFolderBox.Text;
                    if (dialog.ShowDialog(form) != DialogResult.OK) return;
                    _clientFolderBox.Text = dialog.SelectedPath;
                    ClientFolder = dialog.SelectedPath;
                    if (string.IsNullOrWhiteSpace(_clientExeBox.Text) || !File.Exists(_clientExeBox.Text))
                        _clientExeBox.Text = Path.Combine(dialog.SelectedPath, "skidrush.exe");
                    ClientExecutable = _clientExeBox.Text;
                    SaveSettings();
                    RefreshClientStatus();
                }
            };
            card.Controls.Add(browseFolder);

            _clientExeBox = MakePathBox(ClientExecutable, new Point(20, 111), 660);
            card.Controls.Add(_clientExeBox);
            var browseExe = MakeSmallButton("SKIDRUSH.EXE", new Point(692, 110), 170);
            browseExe.Click += delegate
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "Select skidrush.exe";
                    dialog.Filter = "SkidRush client|skidrush.exe|Executable files|*.exe|All files|*.*";
                    if (Directory.Exists(ClientFolder)) dialog.InitialDirectory = ClientFolder;
                    if (dialog.ShowDialog(form) != DialogResult.OK) return;
                    _clientExeBox.Text = dialog.FileName;
                    ClientExecutable = dialog.FileName;
                    ClientFolder = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
                    _clientFolderBox.Text = ClientFolder;
                    SaveSettings();
                    RefreshClientStatus();
                }
            };
            card.Controls.Add(browseExe);

            _clientFolderBox.TextChanged += delegate { ClientFolder = _clientFolderBox.Text.Trim(); SaveSettings(); };
            _clientExeBox.TextChanged += delegate { ClientExecutable = _clientExeBox.Text.Trim(); SaveSettings(); };

            var redirect = MakeClientCheckBox("Redirect " + ClientLauncher.OfficialServerIp + " to this PC", RedirectOfficialClient, new Point(20, 151));
            redirect.CheckedChanged += delegate { RedirectOfficialClient = redirect.Checked; SaveSettings(); RefreshClientStatus(); };
            card.Controls.Add(redirect);

            var startServers = MakeClientCheckBox("Start all servers before game", StartAllBeforeGame, new Point(300, 151));
            startServers.CheckedChanged += delegate { StartAllBeforeGame = startServers.Checked; SaveSettings(); };
            card.Controls.Add(startServers);

            var restore = MakeClientCheckBox("Remove redirect when game closes", RestoreRedirectOnClientExit, new Point(535, 151));
            restore.CheckedChanged += delegate { RestoreRedirectOnClientExit = restore.Checked; SaveSettings(); };
            card.Controls.Add(restore);

            var test = MakeSmallButton("TEST CLIENT", new Point(20, 188), 140);
            test.Click += delegate
            {
                SyncClientPathsFromUi();
                var validation = ClientLauncher.ValidateClient(ClientFolder, ClientExecutable);
                if (validation == null)
                    SetClientStatus("Client ready. " + (ClientLauncher.IsAdministrator() ? "Administrator mode detected." : "Run Manager as Administrator for IP redirect."),
                        ClientLauncher.IsAdministrator() ? RunningColor : WarningColor);
                else
                    SetClientStatus(validation, StoppedColor);
            };
            card.Controls.Add(test);

            _clientStatus = new Label
            {
                AutoSize = false, ForeColor = MutedColor, Font = new Font("Segoe UI", 9F),
                Location = new Point(180, 194), Size = new Size(690, 35)
            };
            card.Controls.Add(_clientStatus);
            return card;
        }

        private static TextBox MakePathBox(string text, Point location, int width)
        {
            return new TextBox
            {
                Text = text ?? string.Empty,
                Location = location,
                Size = new Size(width, 27),
                BackColor = Color.FromArgb(12, 15, 19),
                ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F)
            };
        }

        private static Button MakeSmallButton(string text, Point location, int width)
        {
            var button = new Button
            {
                Text = text, Location = location, Size = new Size(width, 29),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(26, 31, 38),
                ForeColor = TextColor, Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                UseVisualStyleBackColor = false, Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = BorderColor;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 40, 49);
            return button;
        }

        private static CheckBox MakeClientCheckBox(string text, bool value, Point location)
        {
            return new CheckBox
            {
                AutoSize = true, Text = text, Checked = value, ForeColor = TextColor,
                BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.8F),
                Location = location, Cursor = Cursors.Hand
            };
        }

        private static void AttachGameStartButton(MainForm form)
        {
            Label title = null;
            foreach (var label in FindControls<Label>(form))
            {
                if (string.Equals(label.Text, "DRIFT CITY SERVER MANAGER", StringComparison.OrdinalIgnoreCase))
                {
                    title = label;
                    break;
                }
            }
            if (title == null || title.Parent == null) return;

            var header = title.Parent;
            Button startAll = null;
            foreach (Control control in header.Controls)
            {
                var button = control as Button;
                if (button != null && string.Equals(button.Text, "START ALL", StringComparison.OrdinalIgnoreCase))
                {
                    startAll = button;
                    break;
                }
            }

            _gameStartButton = new Button
            {
                Text = "GAME START",
                Width = 132,
                Height = 38,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(22, 43, 67),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            _gameStartButton.FlatAppearance.BorderColor = Color.FromArgb(67, 116, 166);
            _gameStartButton.FlatAppearance.BorderSize = 1;
            _gameStartButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(29, 54, 82);
            _gameStartButton.Click += delegate { LaunchConfiguredClient(form); };
            header.Controls.Add(_gameStartButton);

            Action reposition = delegate
            {
                if (_gameStartButton == null || _gameStartButton.IsDisposed) return;
                if (startAll != null)
                    _gameStartButton.Location = new Point(startAll.Left - _gameStartButton.Width - 10, 17);
                else
                    _gameStartButton.Location = new Point(header.ClientSize.Width - _gameStartButton.Width - 280, 17);
            };
            header.Resize += delegate { reposition(); };
            reposition();
        }

        private static void LaunchConfiguredClient(MainForm form)
        {
            SyncClientPathsFromUi();
            var validation = ClientLauncher.ValidateClient(ClientFolder, ClientExecutable);
            if (validation != null)
            {
                SetClientStatus(validation, StoppedColor);
                MessageBox.Show(form, validation, "Game Client", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (_gameStartButton != null) _gameStartButton.Enabled = false;
                SetClientStatus("Preparing game client...", WarningColor);

                if (StartAllBeforeGame)
                    InvokePrivateMethod(form, "StartAll");

                if (RedirectOfficialClient)
                {
                    if (!ClientLauncher.IsAdministrator())
                        throw new InvalidOperationException("IP redirect requires Administrator privileges. Close DCServerManager and Run as administrator.");
                    ClientLauncher.EnableRedirect(delegate(string message) { LogClient(form, message); });
                }

                ClientLauncher.StartClient(
                    ClientFolder,
                    ClientExecutable,
                    delegate(string message) { LogClient(form, message); },
                    delegate
                    {
                        if (RestoreRedirectOnClientExit)
                            ClientLauncher.DisableRedirect(delegate(string message) { LogClient(form, message); });
                        try
                        {
                            form.BeginInvoke(new Action(delegate
                            {
                                if (_gameStartButton != null) _gameStartButton.Enabled = true;
                                SetClientStatus("Game client stopped.", MutedColor);
                            }));
                        }
                        catch { }
                    });

                SetClientStatus("Game running. Redirect " + (RedirectOfficialClient ? "ACTIVE" : "disabled") + ".", RunningColor);
            }
            catch (Exception ex)
            {
                if (RedirectOfficialClient && RestoreRedirectOnClientExit)
                    ClientLauncher.DisableRedirect(delegate(string message) { LogClient(form, message); });
                if (_gameStartButton != null) _gameStartButton.Enabled = true;
                SetClientStatus("Game start failed: " + ex.Message, StoppedColor);
                MessageBox.Show(form, ex.Message, "Game Start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void LogClient(MainForm form, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            Debug.WriteLine(message);
            try
            {
                if (form.InvokeRequired)
                {
                    form.BeginInvoke(new Action(delegate { LogClient(form, message); }));
                    return;
                }
                SetClientStatus(message, message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ? StoppedColor : RunningColor);
            }
            catch { }
        }

        private static void RefreshClientStatus()
        {
            if (_clientStatus == null) return;
            SyncClientPathsFromUi();
            var validation = ClientLauncher.ValidateClient(ClientFolder, ClientExecutable);
            if (validation != null)
            {
                SetClientStatus(validation, WarningColor);
                return;
            }

            if (ClientLauncher.IsClientRunning)
            {
                SetClientStatus("skidrush.exe is running.", RunningColor);
                return;
            }

            SetClientStatus(
                "Client ready. Official " + ClientLauncher.OfficialServerIp + " → local " + ClientLauncher.LocalServerIp +
                (RedirectOfficialClient && !ClientLauncher.IsAdministrator() ? " (Administrator required for redirect)" : string.Empty),
                RedirectOfficialClient && !ClientLauncher.IsAdministrator() ? WarningColor : RunningColor);
        }

        private static void SetClientStatus(string text, Color color)
        {
            if (_clientStatus == null) return;
            _clientStatus.Text = text;
            _clientStatus.ForeColor = color;
        }

        private static void SyncClientPathsFromUi()
        {
            if (_clientFolderBox != null) ClientFolder = _clientFolderBox.Text.Trim();
            if (_clientExeBox != null) ClientExecutable = _clientExeBox.Text.Trim();
        }

        private static void InvokePrivateMethod(MainForm form, string name)
        {
            var method = typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException("MainForm", name);
            method.Invoke(form, null);
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
            ClientFolder = string.Empty;
            ClientExecutable = string.Empty;
            RedirectOfficialClient = true;
            StartAllBeforeGame = true;
            RestoreRedirectOnClientExit = true;

            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    if (key.Equals("ClearVisibleLogsOnServerStart", StringComparison.OrdinalIgnoreCase))
                    {
                        bool parsed;
                        if (bool.TryParse(value, out parsed)) ClearVisibleLogsOnServerStart = parsed;
                    }
                    else if (key.Equals("ClientFolder", StringComparison.OrdinalIgnoreCase)) ClientFolder = value;
                    else if (key.Equals("ClientExecutable", StringComparison.OrdinalIgnoreCase)) ClientExecutable = value;
                    else if (key.Equals("RedirectOfficialClient", StringComparison.OrdinalIgnoreCase)) RedirectOfficialClient = ParseBool(value, true);
                    else if (key.Equals("StartAllBeforeGame", StringComparison.OrdinalIgnoreCase)) StartAllBeforeGame = ParseBool(value, true);
                    else if (key.Equals("RestoreRedirectOnClientExit", StringComparison.OrdinalIgnoreCase)) RestoreRedirectOnClientExit = ParseBool(value, true);
                }
            }
            catch { }
        }

        private static bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            return bool.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath,
                    "ClearVisibleLogsOnServerStart=" + ClearVisibleLogsOnServerStart + Environment.NewLine +
                    "ClientFolder=" + (ClientFolder ?? string.Empty) + Environment.NewLine +
                    "ClientExecutable=" + (ClientExecutable ?? string.Empty) + Environment.NewLine +
                    "RedirectOfficialClient=" + RedirectOfficialClient + Environment.NewLine +
                    "StartAllBeforeGame=" + StartAllBeforeGame + Environment.NewLine +
                    "RestoreRedirectOnClientExit=" + RestoreRedirectOnClientExit + Environment.NewLine);
            }
            catch { }
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
