using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ServerManager
{
    public sealed class MainForm : Form
    {
        private sealed class ServerEntry
        {
            public string Name;
            public string ExeName;
            public Process Process;
            public Label StatusLabel;
            public Button ToggleButton;
            public RichTextBox LogBox;
            public TextBox CommandBox;
        }

        private readonly Dictionary<string, ServerEntry> _servers = new Dictionary<string, ServerEntry>();
        private readonly FlowLayoutPanel _serverCards = new FlowLayoutPanel();
        private readonly TabControl _logTabs = new TabControl();
        private readonly Timer _statusTimer = new Timer();
        private readonly Label _summaryLabel = new Label();

        private static readonly Color BackgroundColor = Color.FromArgb(24, 26, 31);
        private static readonly Color PanelColor = Color.FromArgb(34, 37, 43);
        private static readonly Color LogColor = Color.FromArgb(17, 19, 23);
        private static readonly Color TextColor = Color.FromArgb(226, 229, 234);
        private static readonly Color MutedColor = Color.FromArgb(150, 157, 168);
        private static readonly Color RunningColor = Color.FromArgb(70, 190, 105);
        private static readonly Color StoppedColor = Color.FromArgb(220, 75, 75);
        private static readonly Color WarningColor = Color.FromArgb(238, 181, 73);
        private static readonly Color DebugColor = Color.FromArgb(125, 155, 195);

        public MainForm()
        {
            Text = "Drift City Server Manager";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1050, 700);
            Size = new Size(1280, 820);
            BackColor = BackgroundColor;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            BuildLayout();
            AddServer("Auth", "AuthServer.exe");
            AddServer("Lobby", "LobbyServer.exe");
            AddServer("Game", "GameServer.exe");
            AddServer("Area", "AreaServer.exe");
            AddServer("Ranking", "RankingServer.exe");

            _statusTimer.Interval = 750;
            _statusTimer.Tick += delegate { RefreshStatuses(); };
            _statusTimer.Start();

            Shown += delegate { RefreshStatuses(); };
            FormClosing += MainForm_FormClosing;
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12),
                BackColor = BackgroundColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = BackgroundColor };
            root.Controls.Add(header, 0, 0);

            var title = new Label
            {
                AutoSize = true,
                Text = "DRIFT CITY SERVER MANAGER",
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(0, 3)
            };
            header.Controls.Add(title);

            _summaryLabel.AutoSize = true;
            _summaryLabel.ForeColor = MutedColor;
            _summaryLabel.Location = new Point(3, 35);
            _summaryLabel.Text = "0/5 servers running";
            header.Controls.Add(_summaryLabel);

            var stopAll = MakeHeaderButton("STOP ALL", StoppedColor);
            stopAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            stopAll.Location = new Point(header.Width - 120, 8);
            stopAll.Click += delegate { StopAll(); };
            header.Controls.Add(stopAll);

            var startAll = MakeHeaderButton("START ALL", RunningColor);
            startAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            startAll.Location = new Point(header.Width - 245, 8);
            startAll.Click += delegate { StartAll(); };
            header.Controls.Add(startAll);

            header.Resize += delegate
            {
                stopAll.Left = header.ClientSize.Width - stopAll.Width;
                startAll.Left = stopAll.Left - startAll.Width - 8;
            };

            _serverCards.Dock = DockStyle.Fill;
            _serverCards.FlowDirection = FlowDirection.LeftToRight;
            _serverCards.WrapContents = false;
            _serverCards.AutoScroll = true;
            _serverCards.BackColor = BackgroundColor;
            root.Controls.Add(_serverCards, 0, 1);

            _logTabs.Dock = DockStyle.Fill;
            _logTabs.Padding = new Point(14, 6);
            root.Controls.Add(_logTabs, 0, 2);
        }

        private Button MakeHeaderButton(string text, Color backColor)
        {
            return new Button
            {
                Text = text,
                Width = 115,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
        }

        private void AddServer(string name, string exeName)
        {
            var entry = new ServerEntry
            {
                Name = name,
                ExeName = exeName
            };

            var card = new Panel
            {
                Width = 225,
                Height = 94,
                Margin = new Padding(0, 4, 10, 4),
                BackColor = PanelColor
            };

            var nameLabel = new Label
            {
                Text = name + " Server",
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
                Location = new Point(12, 10)
            };
            card.Controls.Add(nameLabel);

            entry.StatusLabel = new Label
            {
                Text = "STOPPED",
                AutoSize = true,
                ForeColor = StoppedColor,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                Location = new Point(13, 38)
            };
            card.Controls.Add(entry.StatusLabel);

            entry.ToggleButton = new Button
            {
                Text = "START",
                Width = 78,
                Height = 29,
                Location = new Point(134, 54),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 59, 68),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            entry.ToggleButton.Click += delegate
            {
                if (IsRunning(entry)) StopServer(entry);
                else StartServer(entry);
            };
            card.Controls.Add(entry.ToggleButton);
            _serverCards.Controls.Add(card);

            var tab = new TabPage(name + " Log") { BackColor = LogColor, ForeColor = TextColor };
            _logTabs.TabPages.Add(tab);

            var tabLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = LogColor,
                Padding = new Padding(4)
            };
            tabLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tabLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            tab.Controls.Add(tabLayout);

            entry.LogBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = LogColor,
                ForeColor = TextColor,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5F),
                HideSelection = false,
                WordWrap = false,
                DetectUrls = false
            };
            tabLayout.Controls.Add(entry.LogBox, 0, 0);

            var commandPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = PanelColor,
                Padding = new Padding(6, 5, 6, 5)
            };
            commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
            tabLayout.Controls.Add(commandPanel, 0, 1);

            entry.CommandBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(25, 28, 33),
                ForeColor = Color.White,
                Font = new Font("Consolas", 9.5F)
            };
            entry.CommandBox.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendCommand(entry);
                }
            };
            commandPanel.Controls.Add(entry.CommandBox, 0, 0);

            var sendButton = new Button
            {
                Dock = DockStyle.Fill,
                Text = "SEND",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 59, 68),
                ForeColor = Color.White,
                UseVisualStyleBackColor = false
            };
            sendButton.Click += delegate { SendCommand(entry); };
            commandPanel.Controls.Add(sendButton, 1, 0);

            _servers.Add(name, entry);
        }

        private void StartAll()
        {
            foreach (var entry in _servers.Values)
                if (!IsRunning(entry))
                    StartServer(entry);
        }

        private void StopAll()
        {
            foreach (var entry in _servers.Values)
                if (IsRunning(entry))
                    StopServer(entry);
        }

        private void StartServer(ServerEntry entry)
        {
            if (IsRunning(entry)) return;

            var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, entry.ExeName);
            if (!File.Exists(exePath))
            {
                AppendManagerLog(entry, "Executable not found: " + exePath, StoppedColor);
                MessageBox.Show(this,
                    entry.ExeName + " was not found next to DCServerManager.exe.\r\n\r\nBuild the full solution first.",
                    "Server executable not found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                };

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null) AppendServerLog(entry, args.Data, false);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null) AppendServerLog(entry, args.Data, true);
                };
                process.Exited += delegate
                {
                    AppendManagerLog(entry, "Process exited.", StoppedColor);
                    BeginInvoke(new Action(RefreshStatuses));
                };

                if (!process.Start())
                {
                    AppendManagerLog(entry, "Could not start process.", StoppedColor);
                    process.Dispose();
                    return;
                }

                entry.Process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                AppendManagerLog(entry, "Started " + entry.ExeName + " (PID " + process.Id + ").", RunningColor);
                RefreshStatuses();
            }
            catch (Exception ex)
            {
                AppendManagerLog(entry, "Start failed: " + ex, StoppedColor);
                MessageBox.Show(this, ex.Message, "Could not start " + entry.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopServer(ServerEntry entry)
        {
            try
            {
                if (entry.Process != null && !entry.Process.HasExited)
                {
                    try
                    {
                        entry.Process.StandardInput.WriteLine("exit");
                        entry.Process.StandardInput.Flush();
                    }
                    catch
                    {
                    }

                    if (!entry.Process.WaitForExit(1000))
                        entry.Process.Kill();

                    AppendManagerLog(entry, "Stopped by Server Manager.", WarningColor);
                }
                else
                {
                    var processName = Path.GetFileNameWithoutExtension(entry.ExeName);
                    foreach (var external in Process.GetProcessesByName(processName))
                    {
                        try { external.Kill(); }
                        catch { }
                        finally { external.Dispose(); }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendManagerLog(entry, "Stop failed: " + ex.Message, StoppedColor);
            }
            finally
            {
                RefreshStatuses();
            }
        }

        private void SendCommand(ServerEntry entry)
        {
            var command = entry.CommandBox.Text.Trim();
            if (command.Length == 0) return;

            if (entry.Process == null || entry.Process.HasExited)
            {
                AppendManagerLog(entry, "Cannot send command: this process was not started by Server Manager.", WarningColor);
                return;
            }

            try
            {
                entry.Process.StandardInput.WriteLine(command);
                entry.Process.StandardInput.Flush();
                AppendManagerLog(entry, "> " + command, DebugColor);
                entry.CommandBox.Clear();
            }
            catch (Exception ex)
            {
                AppendManagerLog(entry, "Command failed: " + ex.Message, StoppedColor);
            }
        }

        private bool IsRunning(ServerEntry entry)
        {
            try
            {
                if (entry.Process != null && !entry.Process.HasExited)
                    return true;
            }
            catch
            {
            }

            var processName = Path.GetFileNameWithoutExtension(entry.ExeName);
            return Process.GetProcessesByName(processName).Length > 0;
        }

        private bool IsExternalRunning(ServerEntry entry)
        {
            try
            {
                if (entry.Process != null && !entry.Process.HasExited)
                    return false;
            }
            catch
            {
            }

            var processName = Path.GetFileNameWithoutExtension(entry.ExeName);
            return Process.GetProcessesByName(processName).Length > 0;
        }

        private void RefreshStatuses()
        {
            if (IsDisposed) return;

            var runningCount = 0;
            foreach (var entry in _servers.Values)
            {
                var running = IsRunning(entry);
                if (running) runningCount++;

                entry.StatusLabel.Text = running
                    ? (IsExternalRunning(entry) ? "RUNNING (EXTERNAL)" : "RUNNING")
                    : "STOPPED";
                entry.StatusLabel.ForeColor = running ? RunningColor : StoppedColor;
                entry.ToggleButton.Text = running ? "STOP" : "START";
                entry.ToggleButton.BackColor = running
                    ? Color.FromArgb(86, 54, 57)
                    : Color.FromArgb(46, 82, 60);
            }

            _summaryLabel.Text = runningCount + "/" + _servers.Count + " servers running";
            _summaryLabel.ForeColor = runningCount == _servers.Count ? RunningColor : MutedColor;
        }

        private void AppendServerLog(ServerEntry entry, string line, bool stderr)
        {
            var color = GetLogColor(line, stderr);
            AppendManagerLog(entry, line, color);
        }

        private Color GetLogColor(string line, bool stderr)
        {
            if (stderr) return StoppedColor;
            var text = (line ?? string.Empty).ToLowerInvariant();

            if (text.Contains("[error]") || text.Contains("exception") || text.Contains("fatal") ||
                text.Contains("killing off client") || text.Contains("stacktrace"))
                return StoppedColor;

            if (text.Contains("[warning]") || text.Contains("warning") || text.Contains("warn:"))
                return WarningColor;

            if (text.Contains("[debug]") || text.Contains("hexdump"))
                return DebugColor;

            if (text.Contains("[info]") || text.Contains("started") || text.Contains("accepted client"))
                return TextColor;

            return Color.FromArgb(196, 201, 210);
        }

        private void AppendManagerLog(ServerEntry entry, string line, Color color)
        {
            if (entry.LogBox.IsDisposed) return;

            if (entry.LogBox.InvokeRequired)
            {
                try
                {
                    entry.LogBox.BeginInvoke(new Action<ServerEntry, string, Color>(AppendManagerLog), entry, line, color);
                }
                catch
                {
                }
                return;
            }

            entry.LogBox.SelectionStart = entry.LogBox.TextLength;
            entry.LogBox.SelectionLength = 0;
            entry.LogBox.SelectionColor = color;
            entry.LogBox.AppendText(line + Environment.NewLine);
            entry.LogBox.SelectionColor = entry.LogBox.ForeColor;
            entry.LogBox.ScrollToCaret();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            var ownedRunning = _servers.Values.Any(delegate(ServerEntry server)
            {
                try { return server.Process != null && !server.Process.HasExited; }
                catch { return false; }
            });

            if (!ownedRunning) return;

            var result = MessageBox.Show(this,
                "There are servers still running. Stop all servers before closing?",
                "Drift City Server Manager",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == DialogResult.Yes)
                StopAll();
        }
    }
}
