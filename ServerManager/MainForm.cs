using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
            public Panel Card;
        }

        private sealed class DarkTabControl : TabControl
        {
            private readonly Color _background;
            private readonly Color _active;
            private readonly Color _inactive;
            private readonly Color _border;
            private readonly Color _text;
            private readonly Color _muted;
            private readonly Color _accent;

            public DarkTabControl(Color background, Color active, Color inactive, Color border, Color text, Color muted, Color accent)
            {
                _background = background;
                _active = active;
                _inactive = inactive;
                _border = border;
                _text = text;
                _muted = muted;
                _accent = accent;

                DrawMode = TabDrawMode.OwnerDrawFixed;
                SizeMode = TabSizeMode.Fixed;
                ItemSize = new Size(130, 36);
                Padding = new Point(0, 0);
            }

            protected override void OnPaintBackground(PaintEventArgs pevent)
            {
                pevent.Graphics.Clear(_background);
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                var selected = e.Index == SelectedIndex;
                var rect = GetTabRect(e.Index);
                rect.Inflate(-1, 0);

                using (var bg = new SolidBrush(selected ? _active : _inactive))
                    e.Graphics.FillRectangle(bg, rect);

                using (var border = new Pen(_border))
                    e.Graphics.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

                if (selected)
                {
                    using (var accent = new SolidBrush(_accent))
                        e.Graphics.FillRectangle(accent, rect.X + 1, rect.Bottom - 3, rect.Width - 2, 3);
                }

                TextRenderer.DrawText(
                    e.Graphics,
                    TabPages[e.Index].Text,
                    new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                    rect,
                    selected ? _text : _muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private readonly Dictionary<string, ServerEntry> _servers = new Dictionary<string, ServerEntry>();
        private readonly FlowLayoutPanel _serverCards = new FlowLayoutPanel();
        private readonly Timer _statusTimer = new Timer();
        private readonly Label _summaryLabel = new Label();
        private readonly DarkTabControl _logTabs;

        private static readonly Color BackgroundColor = Color.FromArgb(12, 14, 18);
        private static readonly Color HeaderColor = Color.FromArgb(16, 18, 23);
        private static readonly Color PanelColor = Color.FromArgb(25, 28, 34);
        private static readonly Color PanelHoverColor = Color.FromArgb(30, 34, 41);
        private static readonly Color SurfaceColor = Color.FromArgb(20, 23, 28);
        private static readonly Color LogColor = Color.FromArgb(9, 11, 14);
        private static readonly Color BorderColor = Color.FromArgb(47, 53, 63);
        private static readonly Color TextColor = Color.FromArgb(236, 239, 243);
        private static readonly Color MutedColor = Color.FromArgb(144, 151, 162);
        private static readonly Color RunningColor = Color.FromArgb(61, 214, 128);
        private static readonly Color StoppedColor = Color.FromArgb(239, 83, 80);
        private static readonly Color WarningColor = Color.FromArgb(242, 184, 72);
        private static readonly Color DebugColor = Color.FromArgb(101, 168, 255);
        private static readonly Color AccentColor = Color.FromArgb(92, 140, 255);

        public MainForm()
        {
            _logTabs = new DarkTabControl(
                BackgroundColor,
                PanelColor,
                SurfaceColor,
                BorderColor,
                TextColor,
                MutedColor,
                AccentColor);

            Text = "Drift City Server Manager";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1100, 720);
            Size = new Size(1320, 850);
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
                Padding = new Padding(18),
                BackColor = BackgroundColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            var header = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = HeaderColor,
                Padding = new Padding(18, 10, 18, 10)
            };
            root.Controls.Add(header, 0, 0);

            var title = new Label
            {
                AutoSize = true,
                Text = "DRIFT CITY SERVER MANAGER",
                Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(18, 10)
            };
            header.Controls.Add(title);

            _summaryLabel.AutoSize = true;
            _summaryLabel.ForeColor = MutedColor;
            _summaryLabel.Font = new Font("Segoe UI", 9.5F);
            _summaryLabel.Location = new Point(21, 47);
            _summaryLabel.Text = "0/5 servers running";
            header.Controls.Add(_summaryLabel);

            var stopAll = MakeHeaderButton("STOP ALL", StoppedColor);
            stopAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            stopAll.Click += delegate { StopAll(); };
            header.Controls.Add(stopAll);

            var startAll = MakeHeaderButton("START ALL", RunningColor);
            startAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            startAll.Click += delegate { StartAll(); };
            header.Controls.Add(startAll);

            header.Resize += delegate
            {
                stopAll.Location = new Point(header.ClientSize.Width - stopAll.Width - 18, 18);
                startAll.Location = new Point(stopAll.Left - startAll.Width - 10, 18);
            };

            _serverCards.Dock = DockStyle.Fill;
            _serverCards.FlowDirection = FlowDirection.LeftToRight;
            _serverCards.WrapContents = false;
            _serverCards.AutoScroll = true;
            _serverCards.BackColor = BackgroundColor;
            _serverCards.Padding = new Padding(0, 10, 0, 8);
            root.Controls.Add(_serverCards, 0, 1);

            var logHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BackgroundColor,
                Padding = new Padding(0, 4, 0, 0)
            };
            root.Controls.Add(logHost, 0, 2);

            _logTabs.Dock = DockStyle.Fill;
            _logTabs.BackColor = BackgroundColor;
            _logTabs.ForeColor = TextColor;
            logHost.Controls.Add(_logTabs);
        }

        private Button MakeHeaderButton(string text, Color accent)
        {
            var button = new Button
            {
                Text = text,
                Width = 118,
                Height = 38,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 33, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = accent;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(39, 43, 51);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(45, 49, 58);
            return button;
        }

        private void AddServer(string name, string exeName)
        {
            var entry = new ServerEntry { Name = name, ExeName = exeName };

            var card = new Panel
            {
                Width = 238,
                Height = 104,
                Margin = new Padding(0, 2, 12, 2),
                Padding = new Padding(14),
                BackColor = PanelColor
            };
            entry.Card = card;

            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            card.MouseEnter += delegate { card.BackColor = PanelHoverColor; };
            card.MouseLeave += delegate { card.BackColor = PanelColor; };

            var nameLabel = new Label
            {
                Text = name + " Server",
                AutoSize = true,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 11.5F, FontStyle.Bold),
                Location = new Point(14, 14)
            };
            card.Controls.Add(nameLabel);

            entry.StatusLabel = new Label
            {
                Text = "STOPPED",
                AutoSize = true,
                ForeColor = StoppedColor,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                Location = new Point(15, 47)
            };
            card.Controls.Add(entry.StatusLabel);

            entry.ToggleButton = new Button
            {
                Text = "START",
                Width = 82,
                Height = 31,
                Location = new Point(142, 58),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 55, 42),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            entry.ToggleButton.FlatAppearance.BorderColor = RunningColor;
            entry.ToggleButton.FlatAppearance.BorderSize = 1;
            entry.ToggleButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 68, 52);
            entry.ToggleButton.Click += delegate
            {
                if (IsRunning(entry)) StopServer(entry);
                else StartServer(entry);
            };
            card.Controls.Add(entry.ToggleButton);
            _serverCards.Controls.Add(card);

            var tab = new TabPage(name + " Log")
            {
                BackColor = LogColor,
                ForeColor = TextColor,
                Padding = new Padding(0)
            };
            _logTabs.TabPages.Add(tab);

            var tabLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = LogColor,
                Padding = new Padding(10)
            };
            tabLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tabLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
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
                DetectUrls = false,
                Margin = new Padding(4)
            };
            tabLayout.Controls.Add(entry.LogBox, 0, 0);

            var commandPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = SurfaceColor,
                Padding = new Padding(8, 7, 8, 7),
                Margin = new Padding(0, 5, 0, 0)
            };
            commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            tabLayout.Controls.Add(commandPanel, 0, 1);

            entry.CommandBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(14, 16, 20),
                ForeColor = TextColor,
                Font = new Font("Consolas", 9.5F),
                Margin = new Padding(0, 1, 8, 1)
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
                BackColor = Color.FromArgb(31, 35, 43),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
                Margin = new Padding(0)
            };
            sendButton.FlatAppearance.BorderColor = BorderColor;
            sendButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 47, 57);
            sendButton.Click += delegate { SendCommand(entry); };
            commandPanel.Controls.Add(sendButton, 1, 0);

            _servers.Add(name, entry);
        }

        private void StartAll()
        {
            foreach (var entry in _servers.Values)
                if (!IsRunning(entry)) StartServer(entry);
        }

        private void StopAll()
        {
            foreach (var entry in _servers.Values)
                if (IsRunning(entry)) StopServer(entry);
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

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

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
                    try { BeginInvoke(new Action(RefreshStatuses)); } catch { }
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
                    catch { }

                    if (!entry.Process.WaitForExit(1000)) entry.Process.Kill();
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
                if (entry.Process != null && !entry.Process.HasExited) return true;
            }
            catch { }

            var processName = Path.GetFileNameWithoutExtension(entry.ExeName);
            var processes = Process.GetProcessesByName(processName);
            try { return processes.Length > 0; }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }

        private bool IsExternalRunning(ServerEntry entry)
        {
            try
            {
                if (entry.Process != null && !entry.Process.HasExited) return false;
            }
            catch { }

            var processName = Path.GetFileNameWithoutExtension(entry.ExeName);
            var processes = Process.GetProcessesByName(processName);
            try { return processes.Length > 0; }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
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
                    ? Color.FromArgb(62, 31, 34)
                    : Color.FromArgb(27, 52, 39);
                entry.ToggleButton.FlatAppearance.BorderColor = running ? StoppedColor : RunningColor;
                entry.ToggleButton.FlatAppearance.MouseOverBackColor = running
                    ? Color.FromArgb(80, 39, 43)
                    : Color.FromArgb(36, 67, 50);
            }

            _summaryLabel.Text = runningCount + "/" + _servers.Count + " servers running";
            _summaryLabel.ForeColor = runningCount == _servers.Count ? RunningColor : MutedColor;
        }

        private void AppendServerLog(ServerEntry entry, string line, bool stderr)
        {
            AppendManagerLog(entry, line, GetLogColor(line, stderr));
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

            if (text.Contains("started ") || text.Contains("network started") || text.Contains("accepted client"))
                return RunningColor;

            if (text.Contains("[info]")) return TextColor;
            return Color.FromArgb(190, 196, 205);
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
                catch { }
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

            if (result == DialogResult.Yes) StopAll();
        }
    }
}
