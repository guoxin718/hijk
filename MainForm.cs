using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace HiJk
{
    public partial class MainForm : Form
    {
        private SystemMonitor monitor;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        public MainForm()
        {
            InitializeComponent();
            InitializeTrayIcon();
            InitializeMonitor();
            SetupForm();
        }

        private void SetupForm()
        {
            this.Text = "HiJk 系统监控";
            this.Size = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;

            // 添加状态显示
            var statusLabel = new Label
            {
                Text = "系统监控正在后台运行...\n日志保存在 Logs 文件夹中",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei", 12)
            };

            this.Controls.Add(statusLabel);
        }

        private void InitializeTrayIcon()
        {
            try
            {
                trayMenu = new ContextMenuStrip();

                var showItem = new ToolStripMenuItem("显示主窗口");
                showItem.Click += OnShow;
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
                            Process.Start("explorer.exe", logPath);
                        }
                    }
                    catch { }
                };
                trayMenu.Items.Add(openLogsItem);

                var exitItem = new ToolStripMenuItem("退出");
                exitItem.Click += OnExit;
                trayMenu.Items.Add(exitItem);

                // 创建自定义图标
                trayIcon = new NotifyIcon
                {
                    Text = "HiJk 系统监控",
                    Icon = SystemIcons.Shield, // 使用系统图标
                    ContextMenuStrip = trayMenu,
                    Visible = true
                };
                trayIcon.DoubleClick += OnShow;

                trayIcon.ShowBalloonTip(3000, "HiJk 系统监控", "监控程序已在后台运行", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化托盘图标失败: {ex.Message}");
            }
        }

        private void InitializeMonitor()
        {
            try
            {
                monitor = new SystemMonitor();
                monitor.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动监控失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnShow(object sender, EventArgs e)
        {
            try
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.BringToFront();
            }
            catch { }
        }

        private void OnExit(object sender, EventArgs e)
        {
            try
            {
                monitor?.Stop();
                trayIcon.Visible = false;
                trayIcon.Dispose();
                Application.Exit();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                trayIcon.ShowBalloonTip(2000, "HiJk 系统监控",
                    "程序已最小化到系统托盘", ToolTipIcon.Info);
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