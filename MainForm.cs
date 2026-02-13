using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
            this.Size = new Size(650, 500);
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

            // 修复换行问题 - 使用 Environment.NewLine 确保正确换行
            var statusLabel = new Label
            {
                Text = "✓ 系统监控正在后台运行" + Environment.NewLine +
                       "✓ 日志保存在 Logs 文件夹中" + Environment.NewLine +
                       "✓ 截图保存在 Screenshots 文件夹中" + Environment.NewLine +
                       "✓ 统计报告保存在 Statistics 文件夹中" + Environment.NewLine +
                       "✓ 右键点击托盘图标查看选项",
                Font = new Font("Microsoft YaHei", 11),
                AutoSize = true,  // 改为 AutoSize
                Location = new Point(20, 60),  // 使用绝对定位
                Padding = new Padding(0),
                TextAlign = ContentAlignment.TopLeft,
                MaximumSize = new Size(this.Width - 80, 0)  // 设置最大宽度
            };

            // 使用绝对定位的Panel来放置状态信息
            var infoPanel = new Panel
            {
                Location = new Point(20, 180),
                Size = new Size(this.Width - 80, 120),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var logPathLabel = new Label
            {
                Text = $"日志路径: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")}",
                Font = new Font("Microsoft YaHei", 9),
                AutoSize = true,
                Location = new Point(0, 0),
                ForeColor = Color.Gray
            };

            var screenshotPathLabel = new Label
            {
                Text = $"截图路径: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots")}",
                Font = new Font("Microsoft YaHei", 9),
                AutoSize = true,
                Location = new Point(0, 25),
                ForeColor = Color.Gray
            };

            var statsPathLabel = new Label
            {
                Text = $"统计路径: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Statistics")}",
                Font = new Font("Microsoft YaHei", 9),
                AutoSize = true,
                Location = new Point(0, 50),
                ForeColor = Color.Gray
            };

            infoPanel.Controls.Add(logPathLabel);
            infoPanel.Controls.Add(screenshotPathLabel);
            infoPanel.Controls.Add(statsPathLabel);

            var buttonPanel = new Panel
            {
                Location = new Point(20, 320),
                Size = new Size(this.Width - 80, 120),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var hideButton = new Button
            {
                Text = "隐藏到托盘",
                Size = new Size(100, 35),
                Location = new Point(0, 0),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightGray,
                FlatStyle = FlatStyle.Flat
            };
            hideButton.Click += (s, e) => HideToTray();

            var openLogsButton = new Button
            {
                Text = "打开日志文件夹",
                Size = new Size(120, 35),
                Location = new Point(110, 0),
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

            var viewStatsButton = new Button
            {
                Text = "查看今日统计分析",
                Size = new Size(150, 35),
                Location = new Point(240, 0),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightGreen,
                FlatStyle = FlatStyle.Flat
            };
            viewStatsButton.Click += (s, e) => ViewTodayStatistics();

            var openScreenshotsButton = new Button
            {
                Text = "打开截图文件夹",
                Size = new Size(120, 35),
                Location = new Point(400, 0),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightSalmon,
                FlatStyle = FlatStyle.Flat
            };
            openScreenshotsButton.Click += (s, e) =>
            {
                try
                {
                    string screenshotPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots");
                    if (!Directory.Exists(screenshotPath))
                    {
                        Directory.CreateDirectory(screenshotPath);
                    }
                    Process.Start("explorer.exe", $"\"{screenshotPath}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"打开截图文件夹失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var exitButton = new Button
            {
                Text = "退出程序",
                Size = new Size(100, 35),
                Location = new Point(530, 0),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightCoral,
                FlatStyle = FlatStyle.Flat
            };
            exitButton.Click += OnExit;

            // 第二行按钮
            var openStatsFolderButton = new Button
            {
                Text = "打开统计文件夹",
                Size = new Size(120, 35),
                Location = new Point(0, 45),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightBlue,
                FlatStyle = FlatStyle.Flat
            };
            openStatsFolderButton.Click += (s, e) =>
            {
                try
                {
                    string statsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Statistics");
                    if (!Directory.Exists(statsPath))
                    {
                        Directory.CreateDirectory(statsPath);
                    }
                    Process.Start("explorer.exe", $"\"{statsPath}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"打开统计文件夹失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            var openTodayScreenshotsButton = new Button
            {
                Text = "打开今日截图",
                Size = new Size(120, 35),
                Location = new Point(130, 45),
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Color.LightYellow,
                FlatStyle = FlatStyle.Flat
            };
            openTodayScreenshotsButton.Click += (s, e) =>
            {
                try
                {
                    string todayFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots", DateTime.Now.ToString("yyyy-MM-dd"));
                    if (!Directory.Exists(todayFolder))
                    {
                        Directory.CreateDirectory(todayFolder);
                    }
                    Process.Start("explorer.exe", $"\"{todayFolder}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"打开今日截图文件夹失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            buttonPanel.Controls.Add(hideButton);
            buttonPanel.Controls.Add(openLogsButton);
            buttonPanel.Controls.Add(viewStatsButton);
            buttonPanel.Controls.Add(openScreenshotsButton);
            buttonPanel.Controls.Add(exitButton);
            buttonPanel.Controls.Add(openStatsFolderButton);
            buttonPanel.Controls.Add(openTodayScreenshotsButton);

            panel.Controls.Add(titleLabel);
            panel.Controls.Add(statusLabel);
            panel.Controls.Add(infoPanel);
            panel.Controls.Add(buttonPanel);

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

                var openScreenshotsItem = new ToolStripMenuItem("打开截图文件夹");
                openScreenshotsItem.Click += (s, e) =>
                {
                    try
                    {
                        string screenshotPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Screenshots");
                        if (!Directory.Exists(screenshotPath))
                        {
                            Directory.CreateDirectory(screenshotPath);
                        }
                        Process.Start("explorer.exe", $"\"{screenshotPath}\"");
                    }
                    catch { }
                };
                trayMenu.Items.Add(openScreenshotsItem);

                var openStatsItem = new ToolStripMenuItem("打开统计文件夹");
                openStatsItem.Click += (s, e) =>
                {
                    try
                    {
                        string statsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Statistics");
                        if (!Directory.Exists(statsPath))
                        {
                            Directory.CreateDirectory(statsPath);
                        }
                        Process.Start("explorer.exe", $"\"{statsPath}\"");
                    }
                    catch { }
                };
                trayMenu.Items.Add(openStatsItem);

                var viewStatsItem = new ToolStripMenuItem("查看今日统计分析");
                viewStatsItem.Click += (s, e) => ViewTodayStatistics();
                trayMenu.Items.Add(viewStatsItem);

                trayMenu.Items.Add(new ToolStripSeparator());

                var aboutItem = new ToolStripMenuItem("关于");
                aboutItem.Click += (s, e) => ShowAbout();
                trayMenu.Items.Add(aboutItem);

                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += OnExit;
                trayMenu.Items.Add(exitItem);

                trayIcon = new NotifyIcon
                {
                    Text = "HiJk 系统监控\n点击显示主窗口",
                    Icon = CreateSystemIcon(),
                    ContextMenuStrip = trayMenu,
                    Visible = true
                };
                trayIcon.DoubleClick += (s, e) => ShowMainWindow();
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
                monitor.SetMainForm(this);
                monitor.Start();
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
            }
            catch { }
        }

        private void ViewTodayStatistics()
        {
            try
            {
                if (monitor == null)
                {
                    MessageBox.Show("监控服务未启动", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string statsContent = monitor.GetTodayStatistics();

                using (var dialog = new Form
                {
                    Text = "今日统计分析报告",
                    Size = new Size(1000, 700),
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
                        Text = statsContent,
                        WordWrap = false
                    };

                    var copyButton = new Button
                    {
                        Text = "复制内容",
                        Size = new Size(100, 30),
                        Location = new Point(10, 10),
                        FlatStyle = FlatStyle.Flat
                    };
                    copyButton.Click += (s, e) =>
                    {
                        Clipboard.SetText(textBox.Text);
                        MessageBox.Show("已复制到剪贴板", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    };

                    var saveButton = new Button
                    {
                        Text = "另存为",
                        Size = new Size(100, 30),
                        Location = new Point(120, 10),
                        FlatStyle = FlatStyle.Flat
                    };
                    saveButton.Click += (s, e) =>
                    {
                        SaveFileDialog saveDialog = new SaveFileDialog();
                        saveDialog.Filter = "文本文件|*.txt|所有文件|*.*";
                        saveDialog.DefaultExt = "txt";
                        saveDialog.FileName = $"Statistics_{DateTime.Now:yyyy-MM-dd}.txt";

                        if (saveDialog.ShowDialog() == DialogResult.OK)
                        {
                            File.WriteAllText(saveDialog.FileName, textBox.Text, Encoding.UTF8);
                            MessageBox.Show("统计报告已保存", "提示",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    };

                    var refreshButton = new Button
                    {
                        Text = "刷新",
                        Size = new Size(100, 30),
                        Location = new Point(230, 10),
                        FlatStyle = FlatStyle.Flat
                    };
                    refreshButton.Click += (s, e) =>
                    {
                        textBox.Text = monitor.GetTodayStatistics();
                    };

                    var panel = new Panel
                    {
                        Height = 50,
                        Dock = DockStyle.Bottom,
                        BackColor = Color.LightGray
                    };
                    panel.Controls.Add(copyButton);
                    panel.Controls.Add(saveButton);
                    panel.Controls.Add(refreshButton);

                    dialog.Controls.Add(panel);
                    dialog.Controls.Add(textBox);
                    dialog.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"查看统计分析失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogToFile("ViewStatisticsError", ex.ToString());
            }
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "HiJk 系统监控 v2.0\n\n" +
                "功能：\n" +
                "• 记录电脑开机/关机时间\n" +
                "• 记录应用程序使用情况\n" +
                "• 记录浏览器访问记录\n" +
                "• 每10分钟自动屏幕截图（包含任务栏）\n" +
                "• 程序启动/关闭时自动截图\n" +
                "• 每日生成统计分析报告\n" +
                "• 实时查看今日统计分析\n\n" +
                "日志保存在 Logs 文件夹\n" +
                "截图保存在 Screenshots 文件夹\n" +
                "统计报告保存在 Statistics 文件夹",
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