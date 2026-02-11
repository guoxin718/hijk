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
        private Dictionary<string, UrlVisit> activeUrlVisits; // 用于去重
        private DateTime lastCheckTime;
        private const string LogDirectory = "Logs";
        private object logLock = new object();
        private IntPtr lastForegroundWindow = IntPtr.Zero;
        private DateTime lastActivityTime = DateTime.Now;
        private bool disposed = false;

        // 浏览器进程名列表（用于过滤）
        private readonly HashSet<string> browserProcesses = new HashSet<string>
        {
            "chrome", "firefox", "msedge", "edge", "iexplore", "opera", "brave", "safari", "vivaldi"
        };

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
            activeUrlVisits = new Dictionary<string, UrlVisit>();
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
                monitorTimer = new System.Timers.Timer(1000); // 1秒检查一次
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

                // 保存所有活动应用的关闭记录（非浏览器应用）
                foreach (var app in runningApplications.Where(a =>
                    a.EndTime == DateTime.MinValue && !IsBrowserProcess(a.ProcessName)))
                {
                    app.EndTime = DateTime.Now;
                    LogApplicationEvent(app, "AutoClosed");
                }

                // 保存所有浏览器会话
                foreach (var session in browserSessions.Values.Where(s => s.EndTime == DateTime.MinValue))
                {
                    session.EndTime = DateTime.Now;
                    // 记录所有URL访问记录
                    foreach (var urlVisit in session.Urls.Where(u => u.EndTime == DateTime.MinValue))
                    {
                        urlVisit.EndTime = DateTime.Now;
                    }
                    LogBrowserEvent(session);
                }

                // 保存所有活动的URL访问记录
                foreach (var urlVisit in activeUrlVisits.Values.Where(u => u.EndTime == DateTime.MinValue))
                {
                    urlVisit.EndTime = DateTime.Now;
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
                var processName = process.ProcessName.ToLower();

                // 检查是否为浏览器进程
                if (IsBrowserProcess(processName))
                {
                    // 浏览器进程由专门的浏览器监控处理
                    return;
                }

                // 检查是否是新的应用（非浏览器）
                var existingApp = runningApplications.FirstOrDefault(a =>
                    a.ProcessId == processId && a.EndTime == DateTime.MinValue);

                if (existingApp == null)
                {
                    // 新应用打开（非浏览器）
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
                foreach (var browserName in browserProcesses)
                {
                    var processes = Process.GetProcessesByName(browserName);

                    if (processes.Any())
                    {
                        if (!browserSessions.ContainsKey(browserName))
                        {
                            // 新浏览器会话开始
                            var session = new BrowserSession
                            {
                                BrowserName = browserName,
                                StartTime = DateTime.Now,
                                EndTime = DateTime.MinValue,
                                Urls = new List<UrlVisit>()
                            };
                            browserSessions[browserName] = session;
                        }

                        // 为每个浏览器进程检查活动标签页
                        foreach (var process in processes)
                        {
                            CheckBrowserWindowTitle(process, browserName);
                        }
                    }
                    else if (browserSessions.ContainsKey(browserName) &&
                             browserSessions[browserName].EndTime == DateTime.MinValue)
                    {
                        // 浏览器已关闭
                        var session = browserSessions[browserName];
                        session.EndTime = DateTime.Now;

                        // 结束所有活动的URL访问记录
                        foreach (var urlVisit in session.Urls.Where(u => u.EndTime == DateTime.MinValue))
                        {
                            urlVisit.EndTime = DateTime.Now;
                        }

                        LogBrowserEvent(session, "BrowserClosed");
                        browserSessions.Remove(browserName);
                    }
                }

                // 清理已结束的URL访问记录
                CleanupUrlVisits();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查浏览器活动失败: {ex.Message}");
            }
        }

        private void CheckBrowserWindowTitle(Process process, string browserName)
        {
            try
            {
                // 获取浏览器窗口标题（通常包含URL信息）
                var windowTitle = GetBrowserWindowTitle(process);

                if (string.IsNullOrEmpty(windowTitle) ||
                    windowTitle.StartsWith("New Tab", StringComparison.OrdinalIgnoreCase) ||
                    windowTitle.StartsWith(browserName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 尝试从窗口标题提取URL
                var url = ExtractUrlFromTitle(windowTitle);
                if (string.IsNullOrEmpty(url))
                {
                    url = windowTitle; // 如果无法提取URL，则使用完整标题
                }

                // 检查是否为重复的URL访问
                var urlKey = $"{browserName}_{url}";

                if (!activeUrlVisits.ContainsKey(urlKey))
                {
                    // 新的URL访问
                    var urlVisit = new UrlVisit
                    {
                        Url = url,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.MinValue
                    };

                    activeUrlVisits[urlKey] = urlVisit;

                    if (browserSessions.ContainsKey(browserName))
                    {
                        browserSessions[browserName].Urls.Add(urlVisit);
                    }

                    // 立即记录URL访问开始
                    LogUrlVisit(urlVisit, browserName, "UrlOpened");
                }
                else
                {
                    // 更新现有URL访问的时间（避免重复记录）
                    var existingVisit = activeUrlVisits[urlKey];
                    if (existingVisit.EndTime != DateTime.MinValue)
                    {
                        // URL已结束，重新开始访问
                        existingVisit.StartTime = DateTime.Now;
                        existingVisit.EndTime = DateTime.MinValue;
                        LogUrlVisit(existingVisit, browserName, "UrlReopened");
                    }
                }

                // 标记其他URL为已结束（浏览器一次只能显示一个标签页）
                var currentUrlKey = urlKey;
                foreach (var kvp in activeUrlVisits.Where(x =>
                    x.Key != currentUrlKey && x.Value.EndTime == DateTime.MinValue).ToList())
                {
                    var otherVisit = kvp.Value;
                    otherVisit.EndTime = DateTime.Now;
                    LogUrlVisit(otherVisit, browserName, "UrlSwitched");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查浏览器窗口失败: {ex.Message}");
            }
        }

        private string GetBrowserWindowTitle(Process process)
        {
            try
            {
                IntPtr mainWindowHandle = process.MainWindowHandle;
                if (mainWindowHandle == IntPtr.Zero)
                {
                    // 尝试查找第一个可见窗口
                    foreach (ProcessThread thread in process.Threads)
                    {
                        // 这里可以添加更复杂的窗口查找逻辑
                    }
                    return string.Empty;
                }

                const int nChars = 1024;
                StringBuilder buff = new StringBuilder(nChars);
                if (GetWindowText(mainWindowHandle, buff, nChars) > 0)
                {
                    return buff.ToString();
                }
            }
            catch { }
            return string.Empty;
        }

        private string ExtractUrlFromTitle(string windowTitle)
        {
            // 尝试从窗口标题提取URL
            // 常见的浏览器标题格式: "页面标题 - 浏览器名" 或 "页面标题"

            // 移除浏览器名后缀
            var browsers = new[] { " - Google Chrome", " - Mozilla Firefox", " - Microsoft Edge", " - Brave", " - Opera" };
            foreach (var browser in browsers)
            {
                if (windowTitle.EndsWith(browser))
                {
                    return windowTitle.Substring(0, windowTitle.Length - browser.Length);
                }
            }

            // 检查是否是URL格式（包含://）
            if (windowTitle.Contains("://"))
            {
                return windowTitle;
            }

            return windowTitle;
        }

        private void CleanupUrlVisits()
        {
            // 清理已结束超过1分钟的URL访问记录
            var oldVisits = activeUrlVisits.Where(kvp =>
                kvp.Value.EndTime != DateTime.MinValue &&
                (DateTime.Now - kvp.Value.EndTime).TotalMinutes > 1).ToList();

            foreach (var kvp in oldVisits)
            {
                activeUrlVisits.Remove(kvp.Key);
            }
        }

        private string GetWindowTitle(IntPtr hWnd)
        {
            try
            {
                const int nChars = 1024;
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

        private bool IsBrowserProcess(string processName)
        {
            return browserProcesses.Contains(processName.ToLower());
        }

        private void OnApplicationOpened(ApplicationInfo app)
        {
            // 如果是浏览器进程，不记录到Applications日志
            if (!IsBrowserProcess(app.ProcessName))
            {
                LogApplicationEvent(app, "Opened");
            }
        }

        private void OnApplicationClosed(ApplicationInfo app)
        {
            // 如果是浏览器进程，不记录到Applications日志
            if (IsBrowserProcess(app.ProcessName))
                return;

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

        private void LogBrowserEvent(BrowserSession session, string eventType = "BrowserActivity")
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
                    logEntry.AppendLine($"总持续时间: {duration.ToString(@"hh\:mm\:ss")}");
                }

                if (session.Urls.Any())
                {
                    logEntry.AppendLine("访问记录:");
                    // 去除重复的URL，只记录不同的URL
                    var distinctUrls = session.Urls
                        .Where(u => u.EndTime != DateTime.MinValue)
                        .GroupBy(u => u.Url)
                        .Select(g => new
                        {
                            Url = g.Key,
                            StartTime = g.Min(u => u.StartTime),
                            EndTime = g.Max(u => u.EndTime),
                            TotalDuration = TimeSpan.FromSeconds(g.Sum(u => (u.EndTime - u.StartTime).TotalSeconds))
                        });

                    foreach (var urlInfo in distinctUrls)
                    {
                        logEntry.AppendLine($"  URL: {urlInfo.Url}");
                        logEntry.AppendLine($"    访问时间: {urlInfo.StartTime:HH:mm:ss} - {urlInfo.EndTime:HH:mm:ss}");
                        logEntry.AppendLine($"    总时长: {urlInfo.TotalDuration.ToString(@"hh\:mm\:ss")}");
                    }
                }
                logEntry.AppendLine(new string('-', 60));

                WriteToLog("Browser", logEntry.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"记录浏览器事件失败: {ex.Message}");
            }
        }

        private void LogUrlVisit(UrlVisit urlVisit, string browserName, string eventType)
        {
            try
            {
                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"事件: {eventType}");
                logEntry.AppendLine($"浏览器: {browserName}");
                logEntry.AppendLine($"URL: {urlVisit.Url}");
                logEntry.AppendLine($"开始时间: {urlVisit.StartTime:HH:mm:ss}");
                logEntry.AppendLine($"结束时间: {(urlVisit.EndTime == DateTime.MinValue ? "访问中" : urlVisit.EndTime.ToString("HH:mm:ss"))}");

                if (urlVisit.EndTime != DateTime.MinValue)
                {
                    var duration = urlVisit.EndTime - urlVisit.StartTime;
                    logEntry.AppendLine($"持续时间: {duration.ToString(@"hh\:mm\:ss")}");
                }
                logEntry.AppendLine(new string('-', 40));

                WriteToLog("Browser", logEntry.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"记录URL访问失败: {ex.Message}");
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