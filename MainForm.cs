using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HiJk
{
    public partial class MainForm : Form
    {
        private SystemMonitor monitor;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private bool allowVisible = false;
        private bool startMinimized = true;

        public MainForm()
        {
            InitializeComponent();
            ProcessStartupArguments();
            InitializeTrayIcon();
            InitializeMonitor();
            SetupForm();

            if (startMinimized)
            {
                HideToTray();
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!allowVisible)
            {
                value = false;
                if (!this.IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        private void ProcessStartupArguments()
        {
            string[] args = Environment.GetCommandLineArgs();

            foreach (string arg in args)
            {
                if (arg.Equals("/show", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--show", StringComparison.OrdinalIgnoreCase))
                {
                    startMinimized = false;
                }
                else if (arg.Equals("/debug", StringComparison.OrdinalIgnoreCase) ||
                         arg.Equals("--debug", StringComparison.OrdinalIgnoreCase))
                {
                    EnableDebugMode();
                }
            }
        }

        private void EnableDebugMode()
        {
            try
            {
                string debugLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "Debug.log");
                if (!File.Exists(debugLogPath))
                {
                    File.Create(debugLogPath).Close();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启用调试模式失败: {ex.Message}");
            }
        }

        private void SetupForm()
        {
            this.Text = "HiJk 系统监控";
            this.Size = new Size(600, 400);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Icon = SystemIcons.Shield;

            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                BackColor = Color.White
            };

            var titleLabel = new Label
            {
                Text = "HiJk 系统监控",
                Font = new Font("Microsoft YaHei", 16, FontStyle.Bold),
                ForeColor = Color.DarkBlue,
                AutoSize = true,
                Dock = DockStyle.Top,
                Height = 40,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var statusLabel = new Label
            {
                Text = "✓ 系统监控正在后台运行\n" +
                       "✓ 日志保存在 Logs 文件夹中\n" +
                       "✓ 右键点击托盘图标查看选项",
                Font = new Font("Microsoft YaHei", 11),
                AutoSize = false,
                Height = 100,
                Padding = new Padding(0, 20, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var logPathLabel = new Label
            {
                Text = $"日志路径: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")}",
                Font = new Font("Microsoft YaHei", 9),
                AutoSize = false,
                Height = 30,
                ForeColor = Color.Gray,
                Padding = new Padding(0, 10, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var infoPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 10, 0, 0)
            };
            infoPanel.Controls.Add(statusLabel);
            infoPanel.Controls.Add(logPathLabel);

            var buttonPanel = new Panel
            {
                Height = 60,
                Dock = DockStyle.Bottom,
                Padding = new Padding(0, 10, 0, 0)
            };

            var hideButton = new Button
            {
                Text = "隐藏到托盘",
                Size = new Size(100, 35),
                Location = new Point(10, 10),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightGray,
                FlatStyle = FlatStyle.Flat
            };
            hideButton.Click += (s, e) => HideToTray();

            var openLogsButton = new Button
            {
                Text = "打开日志文件夹",
                Size = new Size(120, 35),
                Location = new Point(120, 10),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightSkyBlue,
                FlatStyle = FlatStyle.Flat
            };
            openLogsButton.Click += (s, e) =>
            {
                try
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                    if (Directory.Exists(logPath))
                    {
                        Process.Start("explorer.exe", $"\"{logPath}\"");
                    }
                    else
                    {
                        MessageBox.Show("日志文件夹不存在，可能是第一次运行程序。", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"打开日志文件夹失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var exitButton = new Button
            {
                Text = "退出程序",
                Size = new Size(100, 35),
                Location = new Point(250, 10),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightCoral,
                FlatStyle = FlatStyle.Flat
            };
            exitButton.Click += OnExit;

            buttonPanel.Controls.Add(hideButton);
            buttonPanel.Controls.Add(openLogsButton);
            buttonPanel.Controls.Add(exitButton);

            panel.Controls.Add(infoPanel);
            panel.Controls.Add(buttonPanel);
            panel.Controls.Add(titleLabel);

            this.Controls.Add(panel);
        }

        private void InitializeTrayIcon()
        {
            try
            {
                trayMenu = new ContextMenuStrip();

                var showItem = new ToolStripMenuItem("显示主窗口");
                showItem.Click += (s, e) => ShowMainWindow();
                trayMenu.Items.Add(showItem);

                trayMenu.Items.Add(new ToolStripSeparator());

                var openLogsItem = new ToolStripMenuItem("打开日志文件夹");
                openLogsItem.Click += (s, e) =>
                {
                    try
                    {
                        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                        if (Directory.Exists(logPath))
                        {
                            Process.Start("explorer.exe", $"\"{logPath}\"");
                        }
                    }
                    catch { }
                };
                trayMenu.Items.Add(openLogsItem);

                var viewLogsItem = new ToolStripMenuItem("查看今日日志");
                viewLogsItem.Click += (s, e) => ViewTodayLogs();
                trayMenu.Items.Add(viewLogsItem);

                trayMenu.Items.Add(new ToolStripSeparator());

                var aboutItem = new ToolStripMenuItem("关于");
                aboutItem.Click += (s, e) => ShowAbout();
                trayMenu.Items.Add(aboutItem);

                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += OnExit;
                trayMenu.Items.Add(exitItem);

                trayIcon = new NotifyIcon
                {
                    //Text = "HiJk 系统监控\n点击显示主窗口",
                    Icon = CreateSystemIcon(),
                    ContextMenuStrip = trayMenu,
                    Visible = true
                };
                trayIcon.DoubleClick += (s, e) => ShowMainWindow();

                // 移除所有提示：注释掉 ShowBalloonTip 调用
                // 启动时不显示任何提示
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化托盘图标失败: {ex.Message}");
            }
        }

        private Icon CreateSystemIcon()
        {
            try
            {
                return SystemIcons.Shield;
            }
            catch
            {
                using (Bitmap bmp = new Bitmap(16, 16))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Blue);
                    g.FillRectangle(Brushes.White, 4, 4, 8, 8);
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
        }

        private void InitializeMonitor()
        {
            try
            {
                monitor = new SystemMonitor();
                monitor.Start();

                // 移除启动成功提示
                // 完全静默启动
            }
            catch (Exception ex)
            {
                string errorMessage = $"启动监控失败: {ex.Message}\n程序将以受限模式运行。";

                MessageBox.Show(errorMessage, "警告",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

                LogToFile("MonitorStartupError", ex.ToString());
            }
        }

        private void ShowMainWindow()
        {
            try
            {
                allowVisible = true;
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.ShowInTaskbar = true;
                this.BringToFront();
                this.Activate();
            }
            catch { }
        }

        private void HideToTray()
        {
            try
            {
                allowVisible = false;
                this.Hide();
                this.ShowInTaskbar = false;

                // 移除最小化到托盘时的提示
                // 完全静默隐藏
            }
            catch { }
        }

        private void ViewTodayLogs()
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                string today = DateTime.Now.ToString("yyyy-MM-dd");

                if (!Directory.Exists(logPath))
                {
                    MessageBox.Show("日志文件夹不存在，可能还没有生成日志。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var logTypes = new[] { "System", "Applications", "Browser" };
                StringBuilder allLogs = new StringBuilder();
                bool hasLogs = false;

                foreach (var logType in logTypes)
                {
                    string logFile = Path.Combine(logPath, $"{today}_{logType}.log");

                    if (File.Exists(logFile))
                    {
                        hasLogs = true;
                        allLogs.AppendLine($"=== {logType} 日志 ===");
                        string content = File.ReadAllText(logFile, Encoding.UTF8);

                        if (string.IsNullOrWhiteSpace(content))
                        {
                            allLogs.AppendLine("（空）");
                        }
                        else
                        {
                            allLogs.AppendLine(content);
                        }
                        allLogs.AppendLine();
                    }
                }

                if (hasLogs)
                {
                    using (var dialog = new Form
                    {
                        Text = "今日日志",
                        Size = new Size(800, 600),
                        StartPosition = FormStartPosition.CenterParent,
                        MinimizeBox = true,
                        MaximizeBox = true,
                        Icon = this.Icon
                    })
                    {
                        var textBox = new TextBox
                        {
                            Multiline = true,
                            Dock = DockStyle.Fill,
                            ScrollBars = ScrollBars.Both,
                            Font = new Font("Consolas", 10),
                            ReadOnly = true,
                            Text = allLogs.ToString()
                        };

                        var copyButton = new Button
                        {
                            Text = "复制内容",
                            Size = new Size(80, 30),
                            Location = new Point(10, 10),
                            FlatStyle = FlatStyle.Flat
                        };
                        copyButton.Click += (s, e) =>
                        {
                            Clipboard.SetText(textBox.Text);
                            MessageBox.Show("已复制到剪贴板", "提示",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        };

                        var panel = new Panel
                        {
                            Height = 50,
                            Dock = DockStyle.Bottom,
                            BackColor = Color.LightGray
                        };
                        panel.Controls.Add(copyButton);

                        dialog.Controls.Add(panel);
                        dialog.Controls.Add(textBox);
                        dialog.ShowDialog(this);
                    }
                }
                else
                {
                    MessageBox.Show("今日没有找到日志文件", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"查看日志失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "HiJk 系统监控 v1.0\n" +
                "功能：\n" +
                "• 记录电脑开机/关机时间\n" +
                "• 记录应用程序使用情况\n" +
                "• 记录浏览器访问记录\n" +
                "• 每日生成日志文件\n\n" +
                "日志保存在程序目录的 Logs 文件夹中",
                "关于 HiJk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void LogToFile(string category, string message)
        {
            try
            {
                string logDir = "Logs";
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string logFile = Path.Combine(logDir, $"{DateTime.Now:yyyy-MM-dd}_Debug.log");
                string logContent = $"[{DateTime.Now:HH:mm:ss}] {category}: {message}\n";

                File.AppendAllText(logFile, logContent, Encoding.UTF8);
            }
            catch { }
        }

        private void OnExit(object sender, EventArgs e)
        {
            try
            {
                if (MessageBox.Show("确定要退出程序吗？", "确认退出",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    monitor?.Stop();
                    monitor?.Dispose();
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    Application.Exit();
                }
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            monitor?.Dispose();
            trayIcon?.Dispose();
            base.OnClosed(e);
        }
    }
}