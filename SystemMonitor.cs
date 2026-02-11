using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;

namespace HiJk
{
    public class SystemMonitor : IDisposable
    {
        private System.Timers.Timer monitorTimer;
        private SystemEvents systemEvents;
        private List<ApplicationInfo> runningApplications;
        private Dictionary<string, BrowserSession> browserSessions;
        private DateTime lastCheckTime;
        private const string LogDirectory = "Logs";
        private object logLock = new object();
        private IntPtr lastForegroundWindow = IntPtr.Zero;
        private DateTime lastActivityTime = DateTime.Now;
        private bool disposed = false;

        // Windows API声明
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public SystemMonitor()
        {
            runningApplications = new List<ApplicationInfo>();
            browserSessions = new Dictionary<string, BrowserSession>();
            systemEvents = new SystemEvents();
            lastCheckTime = DateTime.Now;
            InitializeLogging();
        }

        public void Start()
        {
            try
            {
                // 记录系统启动
                LogSystemEvent("System", "Startup", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                // 启动监控定时器
                monitorTimer = new System.Timers.Timer(2000); // 2秒检查一次
                monitorTimer.Elapsed += MonitorApplications;
                monitorTimer.AutoReset = true;
                monitorTimer.Enabled = true;

                // 监听系统事件
                systemEvents.Subscribe();
                systemEvents.ApplicationOpened += OnApplicationOpened;
                systemEvents.ApplicationClosed += OnApplicationClosed;

                Debug.WriteLine("系统监控已启动");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动监控失败: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                // 记录系统关机
                LogSystemEvent("System", "Shutdown", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                monitorTimer?.Stop();
                monitorTimer?.Dispose();
                monitorTimer = null;

                // 保存所有活动应用的关闭记录
                foreach (var app in runningApplications.Where(a => a.EndTime == DateTime.MinValue))
                {
                    app.EndTime = DateTime.Now;
                    LogApplicationEvent(app, "AutoClosed");
                }

                // 保存所有浏览器会话
                foreach (var session in browserSessions.Values.Where(s => s.EndTime == DateTime.MinValue))
                {
                    session.EndTime = DateTime.Now;
                    LogBrowserEvent(session);
                }

                // 取消事件订阅
                if (systemEvents != null)
                {
                    systemEvents.ApplicationOpened -= OnApplicationOpened;
                    systemEvents.ApplicationClosed -= OnApplicationClosed;
                    systemEvents.Unsubscribe();
                }

                Debug.WriteLine("系统监控已停止");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止监控失败: {ex.Message}");
            }
        }

        private void InitializeLogging()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                    Debug.WriteLine($"日志目录已创建: {Path.GetFullPath(LogDirectory)}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化日志目录失败: {ex.Message}");
            }
        }

        private void MonitorApplications(object sender, ElapsedEventArgs e)
        {
            try
            {
                CheckActiveWindow();
                CheckBrowserActivity();
                lastCheckTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"监控出错: {ex.Message}");
            }
        }

        private void CheckActiveWindow()
        {
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();

                // 检查窗口是否可见且有效
                if (foregroundWindow == IntPtr.Zero || !IsWindowVisible(foregroundWindow))
                    return;

                // 如果窗口没有变化，只更新时间
                if (foregroundWindow == lastForegroundWindow)
                {
                    if ((DateTime.Now - lastActivityTime).TotalMinutes > 1)
                    {
                        // 更新最后活动时间
                        lastActivityTime = DateTime.Now;
                    }
                    return;
                }

                lastForegroundWindow = foregroundWindow;
                lastActivityTime = DateTime.Now;

                uint processId;
                GetWindowThreadProcessId(foregroundWindow, out processId);

                if (processId == 0) return;

                var process = Process.GetProcessById((int)processId);
                var windowTitle = GetWindowTitle(foregroundWindow);

                // 检查是否是新的应用
                var existingApp = runningApplications.FirstOrDefault(a =>
                    a.ProcessId == processId && a.EndTime == DateTime.MinValue);

                if (existingApp == null)
                {
                    // 新应用打开
                    var newApp = new ApplicationInfo
                    {
                        ProcessName = process.ProcessName,
                        WindowTitle = windowTitle,
                        ProcessId = processId,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.MinValue,
                        FilePath = GetProcessFilePath(process)
                    };

                    runningApplications.Add(newApp);
                    LogApplicationEvent(newApp, "Activated");
                }
                else if (existingApp.WindowTitle != windowTitle)
                {
                    // 窗口标题变化，视为新会话
                    existingApp.EndTime = DateTime.Now;
                    LogApplicationEvent(existingApp, "WindowChanged");

                    var newApp = new ApplicationInfo
                    {
                        ProcessName = existingApp.ProcessName,
                        WindowTitle = windowTitle,
                        ProcessId = existingApp.ProcessId,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.MinValue,
                        FilePath = existingApp.FilePath
                    };

                    runningApplications.Add(newApp);
                    LogApplicationEvent(newApp, "NewWindow");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查活动窗口失败: {ex.Message}");
            }
        }

        private void CheckBrowserActivity()
        {
            try
            {
                var browsers = new[] { "chrome", "firefox", "msedge", "iexplore", "opera" };

                foreach (var browserName in browsers)
                {
                    var processes = Process.GetProcessesByName(browserName);

                    if (processes.Any())
                    {
                        if (!browserSessions.ContainsKey(browserName))
                        {
                            var session = new BrowserSession
                            {
                                BrowserName = browserName,
                                StartTime = DateTime.Now,
                                EndTime = DateTime.MinValue,
                                Urls = new List<UrlVisit>()
                            };
                            browserSessions[browserName] = session;
                            LogBrowserEvent(session, "Started");
                        }
                    }
                    else if (browserSessions.ContainsKey(browserName) &&
                             browserSessions[browserName].EndTime == DateTime.MinValue)
                    {
                        // 浏览器已关闭
                        var session = browserSessions[browserName];
                        session.EndTime = DateTime.Now;
                        LogBrowserEvent(session, "Closed");
                        browserSessions.Remove(browserName);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查浏览器活动失败: {ex.Message}");
            }
        }

        private string GetWindowTitle(IntPtr hWnd)
        {
            try
            {
                const int nChars = 256;
                StringBuilder buff = new StringBuilder(nChars);
                if (GetWindowText(hWnd, buff, nChars) > 0)
                {
                    return buff.ToString();
                }
            }
            catch { }
            return "Unknown";
        }

        private string GetProcessFilePath(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void OnApplicationOpened(ApplicationInfo app)
        {
            LogApplicationEvent(app, "Opened");
        }

        private void OnApplicationClosed(ApplicationInfo app)
        {
            var runningApp = runningApplications.FirstOrDefault(a =>
                a.ProcessId == app.ProcessId && a.EndTime == DateTime.MinValue);

            if (runningApp != null)
            {
                runningApp.EndTime = DateTime.Now;
                LogApplicationEvent(runningApp, "Closed");
                runningApplications.Remove(runningApp);
            }
            else
            {
                app.EndTime = DateTime.Now;
                LogApplicationEvent(app, "Closed");
            }
        }

        private void LogApplicationEvent(ApplicationInfo app, string eventType)
        {
            try
            {
                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"事件: {eventType}");
                logEntry.AppendLine($"进程: {app.ProcessName}");
                logEntry.AppendLine($"标题: {app.WindowTitle}");
                logEntry.AppendLine($"进程ID: {app.ProcessId}");
                logEntry.AppendLine($"开始时间: {app.StartTime:HH:mm:ss}");
                logEntry.AppendLine($"结束时间: {(app.EndTime == DateTime.MinValue ? "运行中" : app.EndTime.ToString("HH:mm:ss"))}");

                if (app.EndTime != DateTime.MinValue)
                {
                    var duration = app.EndTime - app.StartTime;
                    logEntry.AppendLine($"持续时间: {duration.ToString(@"hh\:mm\:ss")}");
                }

                if (!string.IsNullOrEmpty(app.FilePath))
                {
                    logEntry.AppendLine($"路径: {app.FilePath}");
                }
                logEntry.AppendLine(new string('-', 60));

                WriteToLog("Applications", logEntry.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"记录应用事件失败: {ex.Message}");
            }
        }

        private void LogBrowserEvent(BrowserSession session, string eventType = "Activity")
        {
            try
            {
                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"事件: {eventType}");
                logEntry.AppendLine($"浏览器: {session.BrowserName}");
                logEntry.AppendLine($"开始时间: {session.StartTime:HH:mm:ss}");
                logEntry.AppendLine($"结束时间: {(session.EndTime == DateTime.MinValue ? "运行中" : session.EndTime.ToString("HH:mm:ss"))}");

                if (session.EndTime != DateTime.MinValue)
                {
                    var duration = session.EndTime - session.StartTime;
                    logEntry.AppendLine($"持续时间: {duration.ToString(@"hh\:mm\:ss")}");
                }
                logEntry.AppendLine(new string('-', 60));

                WriteToLog("Browser", logEntry.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"记录浏览器事件失败: {ex.Message}");
            }
        }

        private void LogSystemEvent(string category, string eventName, string details)
        {
            try
            {
                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"类别: {category}");
                logEntry.AppendLine($"事件: {eventName}");
                logEntry.AppendLine($"详情: {details}");
                logEntry.AppendLine(new string('-', 60));

                WriteToLog("System", logEntry.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"记录系统事件失败: {ex.Message}");
            }
        }

        private void WriteToLog(string category, string content)
        {
            lock (logLock)
            {
                try
                {
                    string date = DateTime.Now.ToString("yyyy-MM-dd");
                    string logFile = Path.Combine(LogDirectory, $"{date}_{category}.log");

                    // 确保目录存在
                    Directory.CreateDirectory(Path.GetDirectoryName(logFile));

                    File.AppendAllText(logFile, content, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"写入日志失败: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    Stop();
                    systemEvents?.Dispose();
                    monitorTimer?.Dispose();
                }
                disposed = true;
            }
        }

        ~SystemMonitor()
        {
            Dispose(false);
        }
    }

    public class ApplicationInfo
    {
        public string ProcessName { get; set; }
        public string WindowTitle { get; set; }
        public uint ProcessId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string FilePath { get; set; }
    }

    public class BrowserSession
    {
        public string BrowserName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<UrlVisit> Urls { get; set; } = new List<UrlVisit>();
    }

    public class UrlVisit
    {
        public string Url { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }
}