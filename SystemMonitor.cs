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
        private Dictionary<string, UrlVisit> activeUrlVisits;
        private DateTime lastCheckTime;
        private const string LogDirectory = "Logs";
        private object logLock = new object();
        private IntPtr lastForegroundWindow = IntPtr.Zero;
        private DateTime lastActivityTime = DateTime.Now;
        private bool disposed = false;

        private readonly HashSet<string> browserProcesses = new HashSet<string>
        {
            "chrome", "firefox", "msedge", "edge", "iexplore", "opera", "brave", "safari", "vivaldi"
        };

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
                WriteDebugLog("SystemMonitor 开始初始化...");

                LogSystemEvent("System", "Startup", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                monitorTimer = new System.Timers.Timer(1000);
                monitorTimer.Elapsed += MonitorApplications;
                monitorTimer.AutoReset = true;
                monitorTimer.Enabled = true;

                if (systemEvents != null)
                {
                    systemEvents.Subscribe();
                    systemEvents.ApplicationOpened += OnApplicationOpened;
                    systemEvents.ApplicationClosed += OnApplicationClosed;
                }

                WriteDebugLog("SystemMonitor 启动完成");
            }
            catch (Exception ex)
            {
                WriteDebugLog($"启动监控失败: {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            try
            {
                WriteDebugLog("SystemMonitor 正在停止...");

                LogSystemEvent("System", "Shutdown", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                monitorTimer?.Stop();
                monitorTimer?.Dispose();
                monitorTimer = null;

                foreach (var app in runningApplications.Where(a =>
                    a.EndTime == DateTime.MinValue && !IsBrowserProcess(a.ProcessName)))
                {
                    app.EndTime = DateTime.Now;
                    LogApplicationEvent(app, "AutoClosed");
                }

                foreach (var session in browserSessions.Values.Where(s => s.EndTime == DateTime.MinValue))
                {
                    session.EndTime = DateTime.Now;
                    foreach (var urlVisit in session.Urls.Where(u => u.EndTime == DateTime.MinValue))
                    {
                        urlVisit.EndTime = DateTime.Now;
                    }
                    LogBrowserEvent(session);
                }

                foreach (var urlVisit in activeUrlVisits.Values.Where(u => u.EndTime == DateTime.MinValue))
                {
                    urlVisit.EndTime = DateTime.Now;
                }

                if (systemEvents != null)
                {
                    systemEvents.ApplicationOpened -= OnApplicationOpened;
                    systemEvents.ApplicationClosed -= OnApplicationClosed;
                    systemEvents.Unsubscribe();
                }

                WriteDebugLog("SystemMonitor 已停止");
            }
            catch (Exception ex)
            {
                WriteDebugLog($"停止监控失败: {ex.Message}");
            }
        }

        private void InitializeLogging()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                    WriteDebugLog($"日志目录已创建: {Path.GetFullPath(LogDirectory)}");
                }
            }
            catch (Exception ex)
            {
                WriteDebugLog($"初始化日志目录失败: {ex.Message}");
            }
        }

        private void WriteDebugLog(string message)
        {
            try
            {
                string debugLogPath = Path.Combine(LogDirectory, "Debug.log");
                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";
                File.AppendAllText(debugLogPath, logContent, Encoding.UTF8);
            }
            catch
            {
                // 忽略调试日志错误
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
                WriteDebugLog($"监控出错: {ex.Message}");
            }
        }

        private void CheckActiveWindow()
        {
            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();

                if (foregroundWindow == IntPtr.Zero || !IsWindowVisible(foregroundWindow))
                    return;

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

                if (IsBrowserProcess(processName))
                {
                    return;
                }

                var existingApp = runningApplications.FirstOrDefault(a =>
                    a.ProcessId == processId && a.EndTime == DateTime.MinValue);

                if (existingApp == null)
                {
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
                WriteDebugLog($"检查活动窗口失败: {ex.Message}");
            }
        }

        private void CheckBrowserActivity()
        {
            try
            {
                var activeBrowsers = new HashSet<string>();

                foreach (var browserName in browserProcesses)
                {
                    var processes = Process.GetProcessesByName(browserName);

                    if (processes.Any())
                    {
                        activeBrowsers.Add(browserName);

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
                            // 不再记录 BrowserStarted 事件
                        }

                        foreach (var process in processes)
                        {
                            if (!string.IsNullOrEmpty(process.MainWindowTitle))
                            {
                                CheckBrowserWindowTitle(process, browserName);
                            }
                        }
                    }
                }

                var endedBrowsers = browserSessions.Keys
                    .Where(b => !activeBrowsers.Contains(b) && browserSessions[b].EndTime == DateTime.MinValue)
                    .ToList();

                foreach (var browserName in endedBrowsers)
                {
                    var session = browserSessions[browserName];
                    session.EndTime = DateTime.Now;

                    foreach (var urlVisit in session.Urls.Where(u => u.EndTime == DateTime.MinValue))
                    {
                        urlVisit.EndTime = DateTime.Now;
                    }

                    LogBrowserEvent(session, "BrowserClosed");
                    browserSessions.Remove(browserName);
                }

                CleanupUrlVisits();
            }
            catch (Exception ex)
            {
                WriteDebugLog($"检查浏览器活动失败: {ex.Message}");
            }
        }

        private void CheckBrowserWindowTitle(Process process, string browserName)
        {
            try
            {
                string windowTitle = process.MainWindowTitle;

                if (string.IsNullOrEmpty(windowTitle) ||
                    windowTitle.StartsWith("New Tab", StringComparison.OrdinalIgnoreCase) ||
                    windowTitle.StartsWith(browserName, StringComparison.OrdinalIgnoreCase) ||
                    windowTitle == "无标题")
                {
                    return;
                }

                var url = ExtractUrlFromTitle(windowTitle);
                if (string.IsNullOrEmpty(url))
                {
                    url = windowTitle;
                }

                if (url == "无标题" || string.IsNullOrWhiteSpace(url))
                    return;

                var urlKey = $"{browserName}_{url}";

                if (!activeUrlVisits.ContainsKey(urlKey))
                {
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

                    // 只记录 URL 打开事件
                    if (ShouldLogUrlEvent("UrlOpened"))
                    {
                        LogUrlVisit(urlVisit, browserName, "UrlOpened");
                    }
                }
                else
                {
                    var existingVisit = activeUrlVisits[urlKey];
                    if (existingVisit.EndTime != DateTime.MinValue)
                    {
                        // URL 重新打开 - 不记录 UrlReopened 事件
                        existingVisit.StartTime = DateTime.Now;
                        existingVisit.EndTime = DateTime.MinValue;
                        // 不记录 UrlReopened 事件
                    }
                }

                var currentUrlKey = urlKey;
                var otherVisits = activeUrlVisits.Where(x =>
                    x.Key != currentUrlKey && x.Value.EndTime == DateTime.MinValue).ToList();

                foreach (var kvp in otherVisits)
                {
                    var otherVisit = kvp.Value;
                    otherVisit.EndTime = DateTime.Now;

                    // 不记录 UrlSwitched 事件
                }
            }
            catch (Exception ex)
            {
                WriteDebugLog($"检查浏览器窗口失败: {ex.Message}");
            }
        }

        private string ExtractUrlFromTitle(string windowTitle)
        {
            var browsers = new[] {
                " - Google Chrome", " - Mozilla Firefox", " - Microsoft Edge",
                " - Brave", " - Opera", " - Safari", " - Vivaldi"
            };

            foreach (var browser in browsers)
            {
                if (windowTitle.EndsWith(browser))
                {
                    return windowTitle.Substring(0, windowTitle.Length - browser.Length).Trim();
                }
            }

            if (windowTitle.Contains("://"))
            {
                return windowTitle;
            }

            return windowTitle;
        }

        private void CleanupUrlVisits()
        {
            var oldVisits = activeUrlVisits.Where(kvp =>
                kvp.Value.EndTime != DateTime.MinValue &&
                (DateTime.Now - kvp.Value.EndTime).TotalMinutes > 5).ToList();

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
            if (!IsBrowserProcess(app.ProcessName))
            {
                LogApplicationEvent(app, "Opened");
            }
        }

        private void OnApplicationClosed(ApplicationInfo app)
        {
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

                if (!string.IsNullOrEmpty(app.WindowTitle) && app.WindowTitle != "Unknown")
                {
                    logEntry.AppendLine($"标题: {app.WindowTitle}");
                }

                logEntry.AppendLine($"进程ID: {app.ProcessId}");
                logEntry.AppendLine($"开始时间: {app.StartTime:HH:mm:ss}");

                string endTime = app.EndTime == DateTime.MinValue ? "运行中" : app.EndTime.ToString("HH:mm:ss");
                logEntry.AppendLine($"结束时间: {endTime}");

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
                WriteDebugLog($"记录应用事件失败: {ex.Message}");
            }
        }

        private void LogBrowserEvent(BrowserSession session, string eventType = "BrowserActivity")
        {
            try
            {
                // 忽略 BrowserStarted 事件
                if (eventType == "BrowserStarted")
                    return;

                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"事件: {eventType}");
                logEntry.AppendLine($"浏览器: {session.BrowserName}");
                logEntry.AppendLine($"开始时间: {session.StartTime:HH:mm:ss}");

                string endTime = session.EndTime == DateTime.MinValue ? "运行中" : session.EndTime.ToString("HH:mm:ss");
                logEntry.AppendLine($"结束时间: {endTime}");

                if (session.EndTime != DateTime.MinValue)
                {
                    var duration = session.EndTime - session.StartTime;
                    logEntry.AppendLine($"总持续时间: {duration.ToString(@"hh\:mm\:ss")}");
                }

                if (session.Urls.Any())
                {
                    var distinctUrls = session.Urls
                        .Where(u => u.EndTime != DateTime.MinValue &&
                                   !string.IsNullOrEmpty(u.Url) &&
                                   u.Url != "无标题")
                        .GroupBy(u => u.Url)
                        .Select(g => new
                        {
                            Url = g.Key,
                            VisitCount = g.Count(),
                            TotalDuration = TimeSpan.FromSeconds(g.Sum(u => (u.EndTime - u.StartTime).TotalSeconds)),
                            FirstVisit = g.Min(u => u.StartTime),
                            LastVisit = g.Max(u => u.EndTime)
                        })
                        .OrderByDescending(u => u.TotalDuration)
                        .ToList();

                    if (distinctUrls.Any())
                    {
                        logEntry.AppendLine("访问记录:");
                        foreach (var urlInfo in distinctUrls)
                        {
                            logEntry.AppendLine($"  URL: {urlInfo.Url}");
                            logEntry.AppendLine($"    访问次数: {urlInfo.VisitCount}");
                            logEntry.AppendLine($"    总时长: {urlInfo.TotalDuration.ToString(@"hh\:mm\:ss")}");
                            logEntry.AppendLine($"    首次访问: {urlInfo.FirstVisit:HH:mm:ss}");
                            logEntry.AppendLine($"    最后访问: {urlInfo.LastVisit:HH:mm:ss}");
                        }
                    }
                }
                logEntry.AppendLine(new string('-', 60));

                WriteToLog("Browser", logEntry.ToString());
            }
            catch (Exception ex)
            {
                WriteDebugLog($"记录浏览器事件失败: {ex.Message}");
            }
        }

        private void LogUrlVisit(UrlVisit urlVisit, string browserName, string eventType)
        {
            try
            {
                // 过滤不需要的事件类型
                if (ShouldSkipUrlLog(eventType, urlVisit.Url))
                    return;

                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"事件: {eventType}");
                logEntry.AppendLine($"浏览器: {browserName}");

                if (!string.IsNullOrEmpty(urlVisit.Url) && urlVisit.Url != "无标题")
                {
                    logEntry.AppendLine($"URL: {urlVisit.Url}");
                }

                logEntry.AppendLine($"开始时间: {urlVisit.StartTime:HH:mm:ss}");

                string endTime = urlVisit.EndTime == DateTime.MinValue ? "访问中" : urlVisit.EndTime.ToString("HH:mm:ss");
                logEntry.AppendLine($"结束时间: {endTime}");

                if (urlVisit.EndTime != DateTime.MinValue)
                {
                    var duration = urlVisit.EndTime - urlVisit.StartTime;
                    if (duration.TotalSeconds > 0)
                    {
                        logEntry.AppendLine($"持续时间: {duration.ToString(@"hh\:mm\:ss")}");
                    }
                }
                logEntry.AppendLine(new string('-', 40));

                WriteToLog("Browser", logEntry.ToString());
            }
            catch (Exception ex)
            {
                WriteDebugLog($"记录URL访问失败: {ex.Message}");
            }
        }

        private bool ShouldSkipUrlLog(string eventType, string url)
        {
            // 忽略的事件类型
            if (eventType == "UrlSwitched" || eventType == "UrlReopened")
            {
                return true;
            }

            if (eventType == "UrlSwitched")
            {
                if (string.IsNullOrEmpty(url) || url == "无标题")
                    return true;
            }

            return false;
        }

        private bool ShouldLogUrlEvent(string eventType)
        {
            // 只允许记录的事件类型
            string[] allowedEvents = { "UrlOpened", "UrlClosed" };
            return allowedEvents.Contains(eventType);
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
                WriteDebugLog($"记录系统事件失败: {ex.Message}");
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

                    Directory.CreateDirectory(Path.GetDirectoryName(logFile));
                    File.AppendAllText(logFile, content, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    WriteDebugLog($"写入日志失败: {ex.Message}");
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