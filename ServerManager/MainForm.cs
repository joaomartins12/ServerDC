using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            public Panel LogPage;
            public Button TabButton;
        }

        private readonly Dictionary<string, ServerEntry> _servers = new Dictionary<string, ServerEntry>();
        private readonly FlowLayoutPanel _serverCards = new FlowLayoutPanel();
        private readonly FlowLayoutPanel _tabStrip = new FlowLayoutPanel();
        private readonly Panel _logContent = new Panel();
        private readonly Timer _statusTimer = new Timer();
        private readonly Label _summaryLabel = new Label();
        private ServerEntry _activeTab;

        private static readonly Color BackgroundColor = Color.FromArgb(9, 11, 14);
        private static readonly Color HeaderColor = Color.FromArgb(12, 15, 19);
        private static readonly Color PanelColor = Color.FromArgb(20, 24, 30);
        private static readonly Color PanelHoverColor = Color.FromArgb(25, 30, 37);
        private static readonly Color SurfaceColor = Color.FromArgb(14, 17, 22);
        private static readonly Color LogColor = Color.FromArgb(6, 8, 11);
        private static readonly Color BorderColor = Color.FromArgb(45, 51, 60);
        private static readonly Color BorderStrongColor = Color.FromArgb(61, 68, 79);
        private static readonly Color TextColor = Color.FromArgb(232, 235, 239);
        private static readonly Color MutedColor = Color.FromArgb(133, 142, 155);
        private static readonly Color RunningColor = Color.FromArgb(51, 204, 119);
        private static readonly Color StoppedColor = Color.FromArgb(238, 82, 83);
        private static readonly Color WarningColor = Color.FromArgb(236, 180, 71);
        private static readonly Color DebugColor = Color.FromArgb(91, 155, 255);
        private static readonly Color AccentColor = Color.FromArgb(84, 132, 255);

        private const int WmNchittest = 0x84;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const int ResizeBorder = 7;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public MainForm()
        {
            Text = "Drift City Server Manager";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1100, 720);
            Size = new Size(1320, 850);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = BorderColor;
            ForeColor = TextColor;
            Padding = new Padding(1);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;

            BuildLayout();
            AddServer("Auth", "AuthServer.exe");
            AddServer("Lobby", "LobbyServer.exe");
            AddServer("Game", "GameServer.exe");
            AddServer("Area", "AreaServer.exe");
            AddServer("Ranking", "RankingServer.exe");

            if (_servers.Count > 0)
                SelectTab(_servers.Values.First());

            _statusTimer.Interval = 750;
            _statusTimer.Tick += delegate { RefreshStatuses(); };
            _statusTimer.Start();

            Shown += delegate { RefreshStatuses(); };
            FormClosing += MainForm_FormClosing;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNchittest && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    var x = (short)((long)m.LParam & 0xffff);
                    var y = (short)(((long)m.LParam >> 16) & 0xffff);
                    var p = PointToClient(new Point(x, y));

                    var left = p.X <= ResizeBorder;
                    var right = p.X >= ClientSize.Width - ResizeBorder;
                    var top = p.Y <= ResizeBorder;
                    var bottom = p.Y >= ClientSize.Height - ResizeBorder;

                    if (left && top) m.Result = (IntPtr)HtTopLeft;
                    else if (right && top) m.Result = (IntPtr)HtTopRight;
                    else if (left && bottom) m.Result = (IntPtr)HtBottomLeft;
                    else if (right && bottom) m.Result = (IntPtr)HtBottomRight;
                    else if (left) m.Result = (IntPtr)HtLeft;
                    else if (right) m.Result = (IntPtr)HtRight;
                    else if (top) m.Result = (IntPtr)HtTop;
                    else if (bottom) m.Result = (IntPtr)HtBottom;
                }
                return;
            }

            base.WndProc(ref m);
        }

        private void BuildLayout()
        {
            var shell = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BackgroundColor
            };
            Controls.Add(shell);

            var titleBar = BuildTitleBar();
            shell.Controls.Add(titleBar);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(18, 10, 18, 18),
                BackColor = BackgroundColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            shell.Controls.Add(root);
            root.BringToFront();
            root.Padding = new Padding(18, 44, 18, 18);

            var header = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = HeaderColor,
                Padding = new Padding(18, 10, 18, 10)
            };
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(30, 35, 42)))
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            root.Controls.Add(header, 0, 0);

            var title = new Label
            {
                AutoSize = true,
                Text = "DRIFT CITY SERVER MANAGER",
                Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Location = new Point(18, 8)
            };
            header.Controls.Add(title);

            _summaryLabel.AutoSize = true;
            _summaryLabel.ForeColor = MutedColor;
            _summaryLabel.Font = new Font("Segoe UI", 9.5F);
            _summaryLabel.Location = new Point(21, 45);
            _summaryLabel.Text = "0/5 servers running";
            header.Controls.Add(_summaryLabel);

            var stopAll = MakeHeaderButton("STOP ALL", StoppedColor);
            stopAll.Click += delegate { StopAll(); };
            header.Controls.Add(stopAll);

            var startAll = MakeHeaderButton("START ALL", RunningColor);
            startAll.Click += delegate { StartAll(); };
            header.Controls.Add(startAll);

            header.Resize += delegate
            {
                stopAll.Location = new Point(header.ClientSize.Width - stopAll.Width - 18, 17);
                startAll.Location = new Point(stopAll.Left - startAll.Width - 10, 17);
            };

            _serverCards.Dock = DockStyle.Fill;
            _serverCards.FlowDirection = FlowDirection.LeftToRight;
            _serverCards.WrapContents = false;
            _serverCards.AutoScroll = true;
            _serverCards.BackColor = BackgroundColor;
            _serverCards.Padding = new Padding(0, 10, 0, 6);
            root.Controls.Add(_serverCards, 0, 1);

            var logHost = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = BackgroundColor,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            logHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            logHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(logHost, 0, 2);

            _tabStrip.Dock = DockStyle.Fill;
            _tabStrip.FlowDirection = FlowDirection.LeftToRight;
            _tabStrip.WrapContents = false;
            _tabStrip.AutoScroll = false;
            _tabStrip.BackColor = SurfaceColor;
            _tabStrip.Padding = new Padding(0);
            _tabStrip.Margin = new Padding(0);
            _tabStrip.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                    e.Graphics.DrawLine(pen, 0, _tabStrip.Height - 1, _tabStrip.Width, _tabStrip.Height - 1);
            };
            logHost.Controls.Add(_tabStrip, 0, 0);

            _logContent.Dock = DockStyle.Fill;
            _logContent.BackColor = LogColor;
            _logContent.Margin = new Padding(0);
            _logContent.Padding = new Padding(1, 0, 1, 1);
            _logContent.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawLine(pen, 0, 0, 0, _logContent.Height - 1);
                    e.Graphics.DrawLine(pen, _logContent.Width - 1, 0, _logContent.Width - 1, _logContent.Height - 1);
                    e.Graphics.DrawLine(pen, 0, _logContent.Height - 1, _logContent.Width - 1, _logContent.Height - 1);
                }
            };
            logHost.Controls.Add(_logContent, 0, 1);
        }

        private Panel BuildTitleBar()
        {
            var bar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = Color.FromArgb(15, 18, 23)
            };

            bar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(38, 44, 52)))
                    e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };

            var caption = new Label
            {
                AutoSize = true,
                Text = "Drift City Server Manager",
                ForeColor = Color.FromArgb(200, 205, 212),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9F),
                Location = new Point(12, 9)
            };
            bar.Controls.Add(caption);

            var close = MakeWindowButton("×");
            var max = MakeWindowButton("□");
            var min = MakeWindowButton("—");
            close.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 55, 55);

            close.Click += delegate { Close(); };
            max.Click += delegate
            {
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
            };
            min.Click += delegate { WindowState = FormWindowState.Minimized; };

            bar.Controls.Add(close);
            bar.Controls.Add(max);
            bar.Controls.Add(min);

            bar.Resize += delegate
            {
                close.Location = new Point(bar.ClientSize.Width - 46, 0);
                max.Location = new Point(close.Left - 46, 0);
                min.Location = new Point(max.Left - 46, 0);
            };

            MouseEventHandler drag = delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            };
            bar.MouseDown += drag;
            caption.MouseDown += drag;

            caption.DoubleClick += delegate
            {
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
            };
            bar.DoubleClick += delegate
            {
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
            };

            return bar;
        }

        private Button MakeWindowButton(string text)
        {
            var button = new Button
            {
                Text = text,
                Width = 46,
                Height = 33,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(190, 195, 202),
                Font = new Font("Segoe UI", 10F),
                UseVisualStyleBackColor = false,
                TabStop = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 43, 51);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(48, 54, 64);
            return button;
        }

        private Button MakeHeaderButton(string text, Color accent)
        {
            var button = new Button
            {
                Text = text,
                Width = 118,
                Height = 38,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(24, 28, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(66, 73, 84);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(33, 38, 46);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(40, 45, 54);
            button.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(170, accent)))
                    e.Graphics.DrawLine(pen, 12, button.Height - 2, button.Width - 12, button.Height - 2);
            };
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
                BackColor = Color.FromArgb(24, 46, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            entry.ToggleButton.FlatAppearance.BorderColor = Color.FromArgb(58, 93, 72);
            entry.ToggleButton.FlatAppearance.BorderSize = 1;
            entry.ToggleButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 59, 43);
            entry.ToggleButton.Click += delegate
            {
                if (IsRunning(entry)) StopServer(entry);
                else StartServer(entry);
            };
            card.Controls.Add(entry.ToggleButton);
            _serverCards.Controls.Add(card);

            entry.TabButton = new Button
            {
                Text = name + " Log",
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
            entry.TabButton.FlatAppearance.BorderSize = 0;
            entry.TabButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(25, 29, 36);
            entry.TabButton.Click += delegate { SelectTab(entry); };
            _tabStrip.Controls.Add(entry.TabButton);

            entry.LogPage = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = LogColor,
                Padding = new Padding(10),
                Visible = false
            };
            _logContent.Controls.Add(entry.LogPage);

            var tabLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = LogColor,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            tabLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            tabLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            entry.LogPage.Controls.Add(tabLayout);

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
                Margin = new Padding(2)
            };
            tabLayout.Controls.Add(entry.LogBox, 0, 0);

            var commandPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = SurfaceColor,
                Padding = new Padding(8, 8, 8, 7),
                Margin = new Padding(0, 5, 0, 0)
            };
            commandPanel.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(37, 43, 51)))
                    e.Graphics.DrawLine(pen, 0, 0, commandPanel.Width, 0);
            };
            tabLayout.Controls.Add(commandPanel, 0, 1);

            var inputHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 13, 17),
                Padding = new Padding(9, 6, 9, 5)
            };
            inputHost.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(BorderColor))
                    e.Graphics.DrawRectangle(pen, 0, 0, inputHost.Width - 1, inputHost.Height - 1);
            };
            commandPanel.Controls.Add(inputHost);

            var sendButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 96,
                Text = "SEND",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(27, 32, 39),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
                Margin = new Padding(8, 0, 0, 0)
            };
            sendButton.FlatAppearance.BorderColor = BorderStrongColor;
            sendButton.FlatAppearance.BorderSize = 1;
            sendButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(37, 43, 52);
            sendButton.Click += delegate { SendCommand(entry); };
            commandPanel.Controls.Add(sendButton);
            sendButton.BringToFront();

            inputHost.Width -= 104;
            inputHost.Padding = new Padding(9, 7, 9, 5);
            inputHost.Dock = DockStyle.Fill;
            inputHost.Margin = new Padding(0, 0, 104, 0);

            entry.CommandBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(10, 13, 17),
                ForeColor = TextColor,
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
            inputHost.Controls.Add(entry.CommandBox);

            commandPanel.Resize += delegate
            {
                sendButton.Location = new Point(commandPanel.ClientSize.Width - sendButton.Width - 8, 8);
                sendButton.Height = commandPanel.ClientSize.Height - 15;
                inputHost.Location = new Point(8, 8);
                inputHost.Size = new Size(Math.Max(10, sendButton.Left - 16), commandPanel.ClientSize.Height - 15);
            };

            _servers.Add(name, entry);
        }

        private void SelectTab(ServerEntry entry)
        {
            _activeTab = entry;
            foreach (var item in _servers.Values)
            {
                var active = item == entry;
                item.LogPage.Visible = active;
                if (active) item.LogPage.BringToFront();
                item.TabButton.BackColor = active ? PanelColor : SurfaceColor;
                item.TabButton.ForeColor = active ? TextColor : MutedColor;
                item.TabButton.FlatAppearance.MouseOverBackColor = active
                    ? PanelColor
                    : Color.FromArgb(25, 29, 36);
                item.TabButton.Invalidate();
            }

            entry.TabButton.Paint -= TabButtonPaint;
            entry.TabButton.Paint += TabButtonPaint;
        }

        private void TabButtonPaint(object sender, PaintEventArgs e)
        {
            var button = sender as Button;
            if (button == null || _activeTab == null || button != _activeTab.TabButton) return;
            using (var brush = new SolidBrush(AccentColor))
                e.Graphics.FillRectangle(brush, 0, button.Height - 3, button.Width, 3);
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
                    ? Color.FromArgb(52, 27, 30)
                    : Color.FromArgb(24, 46, 34);
                entry.ToggleButton.FlatAppearance.BorderColor = running
                    ? Color.FromArgb(105, 55, 59)
                    : Color.FromArgb(58, 93, 72);
                entry.ToggleButton.FlatAppearance.MouseOverBackColor = running
                    ? Color.FromArgb(67, 33, 37)
                    : Color.FromArgb(31, 59, 43);
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
            return Color.FromArgb(188, 194, 203);
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
