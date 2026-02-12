using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
        private Dictionary<IntPtr, string> knownWindows;
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

        // Windows API 声明
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public SystemMonitor()
        {
            runningApplications = new List<ApplicationInfo>();
            browserSessions = new Dictionary<string, BrowserSession>();
            activeUrlVisits = new Dictionary<string, UrlVisit>();
            knownWindows = new Dictionary<IntPtr, string>();
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

                monitorTimer = new System.Timers.Timer(2000);
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
                        // 不记录UrlClosed事件
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
                var currentWindows = new HashSet<IntPtr>();

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
                            WriteDebugLog($"浏览器 {browserName} 启动");
                        }

                        var browserWindows = GetAllBrowserWindows(processes);

                        foreach (var windowInfo in browserWindows)
                        {
                            currentWindows.Add(windowInfo.HWnd);

                            if (!knownWindows.ContainsKey(windowInfo.HWnd))
                            {
                                knownWindows[windowInfo.HWnd] = windowInfo.Title;
                                WriteDebugLog($"发现新窗口: {windowInfo.Title}");
                            }

                            if (!string.IsNullOrEmpty(windowInfo.Title))
                            {
                                CheckBrowserWindowTitle(windowInfo.Title, browserName, windowInfo.HWnd);
                            }
                        }
                    }
                }

                // 清理已关闭的窗口
                var closedWindows = knownWindows.Keys
                    .Where(hWnd => !currentWindows.Contains(hWnd))
                    .ToList();

                foreach (var hWnd in closedWindows)
                {
                    WriteDebugLog($"窗口关闭: {knownWindows[hWnd]}");
                    knownWindows.Remove(hWnd);
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
                        // 不记录UrlClosed事件
                    }

                    LogBrowserEvent(session);
                    browserSessions.Remove(browserName);
                    WriteDebugLog($"浏览器 {browserName} 关闭");
                }

                CleanupUrlVisits();
            }
            catch (Exception ex)
            {
                WriteDebugLog($"检查浏览器活动失败: {ex.Message}");
            }
        }

        private List<WindowInfo> GetAllBrowserWindows(Process[] processes)
        {
            var windows = new List<WindowInfo>();

            foreach (var process in processes)
            {
                try
                {
                    EnumWindows((hWnd, lParam) =>
                    {
                        GetWindowThreadProcessId(hWnd, out int windowProcessId);

                        if (windowProcessId == process.Id)
                        {
                            if (IsWindowVisible(hWnd))
                            {
                                string title = GetWindowTitle(hWnd);

                                if (!string.IsNullOrEmpty(title))
                                {
                                    StringBuilder className = new StringBuilder(256);
                                    GetClassName(hWnd, className, className.Capacity);

                                    windows.Add(new WindowInfo
                                    {
                                        HWnd = hWnd,
                                        Title = title,
                                        ClassName = className.ToString()
                                    });
                                }
                            }
                        }
                        return true;
                    }, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    WriteDebugLog($"获取浏览器窗口失败: {ex.Message}");
                }
            }

            return windows;
        }

        private void CheckBrowserWindowTitle(string windowTitle, string browserName, IntPtr hWnd)
        {
            try
            {
                // 只过滤完全空白的标题
                if (string.IsNullOrWhiteSpace(windowTitle))
                {
                    return;
                }

                var (pageTitle, url) = ExtractTitleAndUrl(windowTitle, browserName);

                // 确保有URL值
                if (string.IsNullOrEmpty(url))
                {
                    url = "浏览中";
                }

                var urlKey = $"{browserName}_{url}_{hWnd}";

                if (!activeUrlVisits.ContainsKey(urlKey))
                {
                    var urlVisit = new UrlVisit
                    {
                        Url = url,
                        Title = pageTitle,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.MinValue,
                        WindowHandle = hWnd
                    };

                    activeUrlVisits[urlKey] = urlVisit;

                    if (browserSessions.ContainsKey(browserName))
                    {
                        browserSessions[browserName].Urls.Add(urlVisit);
                    }

                    // 检查是否需要记录此URL访问
                    if (ShouldLogUrlVisit(urlVisit))
                    {
                        LogUrlVisit(urlVisit, browserName, "UrlOpened");
                        WriteDebugLog($"新URL访问: {url}, 标题: {pageTitle}");
                    }
                    else
                    {
                        WriteDebugLog($"忽略URL访问: {url}, 标题: {pageTitle}");
                    }
                }
                else
                {
                    var existingVisit = activeUrlVisits[urlKey];
                    if (existingVisit.EndTime != DateTime.MinValue)
                    {
                        // URL重新打开 - 只更新状态，不记录日志
                        existingVisit.Title = pageTitle;
                        existingVisit.StartTime = DateTime.Now;
                        existingVisit.EndTime = DateTime.MinValue;
                        WriteDebugLog($"URL重新打开: {url}, 标题: {pageTitle}");
                    }
                    else
                    {
                        if (existingVisit.Title != pageTitle)
                        {
                            WriteDebugLog($"标题更新: {existingVisit.Title} -> {pageTitle}");
                            existingVisit.Title = pageTitle;
                        }
                    }
                }

                // 检查当前窗口是否是活动窗口
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == hWnd)
                {
                    var otherVisits = activeUrlVisits
                        .Where(x => x.Value.WindowHandle != hWnd &&
                                   x.Value.EndTime == DateTime.MinValue)
                        .ToList();

                    foreach (var kvp in otherVisits)
                    {
                        var otherVisit = kvp.Value;
                        otherVisit.EndTime = DateTime.Now;
                        WriteDebugLog($"窗口切换: {otherVisit.Url} 结束访问");
                        // 不记录UrlSwitched和UrlClosed事件
                    }
                }
            }
            catch (Exception ex)
            {
                WriteDebugLog($"检查浏览器窗口失败: {ex.Message}");
            }
        }

        private bool ShouldLogUrlVisit(UrlVisit urlVisit)
        {
            // 过滤标题为"CandidateWindow"的UrlOpened事件
            if (urlVisit.Title.Contains("CandidateWindow", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 过滤标题为"新标签页"的UrlOpened事件
            if (urlVisit.Title.Contains("新标签页", StringComparison.OrdinalIgnoreCase) ||
                urlVisit.Title.Contains("New Tab", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 过滤URL为"新标签页"的事件
            if (urlVisit.Url.Contains("新标签页", StringComparison.OrdinalIgnoreCase) ||
                urlVisit.Url.Contains("New Tab", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private (string Title, string Url) ExtractTitleAndUrl(string windowTitle, string browserName)
        {
            string title = windowTitle;
            string url = null;

            // 移除浏览器名称后缀
            var browserSuffixes = new[] {
                " - Google Chrome",
                " - Mozilla Firefox",
                " - Microsoft Edge",
                " - Brave",
                " - Opera",
                " - Safari",
                " - Vivaldi",
                " — Google Chrome",
                " — Mozilla Firefox",
                " — Microsoft Edge"
            };

            foreach (var suffix in browserSuffixes)
            {
                if (windowTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    title = windowTitle.Substring(0, windowTitle.Length - suffix.Length).Trim();
                    break;
                }
            }

            // 尝试提取URL
            url = ExtractUrlFromTitle(title);

            // 如果没有提取到URL，根据标题内容分类
            if (string.IsNullOrEmpty(url))
            {
                if (title.Contains("新标签页") || title.Contains("New Tab") || title == "about:blank")
                {
                    url = "新标签页";
                    title = "新标签页";
                }
                else if (title.Contains("设置") || title.Contains("Settings"))
                {
                    url = "浏览器设置";
                }
                else if (title.Contains("历史记录") || title.Contains("History"))
                {
                    url = "历史记录";
                }
                else if (title.Contains("下载") || title.Contains("Downloads"))
                {
                    url = "下载管理";
                }
                else if (title.Contains("书签") || title.Contains("Bookmarks") || title.Contains("Favorites"))
                {
                    url = "书签管理";
                }
                else if (title.Contains("扩展") || title.Contains("Extensions"))
                {
                    url = "扩展管理";
                }
                else
                {
                    // 使用标题作为URL的替代
                    url = title.Length > 30 ? title.Substring(0, 30) + "..." : title;
                }
            }

            return (title, url);
        }

        private string ExtractUrlFromTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;

            // 检查是否是完整URL
            if (IsValidUrl(title))
            {
                return title;
            }

            // 查找URL模式
            string urlPattern = @"(https?://[^\s]+|www\.[^\s]+\.[a-zA-Z]{2,}(/[^\s]*)?)";
            var match = Regex.Match(title, urlPattern, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Value;
            }

            return null;
        }

        private bool IsValidUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            return Uri.TryCreate(text, UriKind.Absolute, out Uri uriResult) &&
                   (uriResult.Scheme == Uri.UriSchemeHttp ||
                    uriResult.Scheme == Uri.UriSchemeHttps);
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
            return string.Empty;
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

                string endTime;
                if (app.EndTime == DateTime.MinValue)
                {
                    endTime = "运行中";
                }
                else
                {
                    endTime = app.EndTime.ToString("HH:mm:ss");
                }
                logEntry.AppendLine($"结束时间: {endTime}");

                if (app.EndTime != DateTime.MinValue)
                {
                    var duration = app.EndTime - app.StartTime;
                    logEntry.AppendLine($"持续时间: {duration:hh\\:mm\\:ss}");
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

        private void LogBrowserEvent(BrowserSession session)
        {
            try
            {
                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"事件: BrowserSession");
                logEntry.AppendLine($"浏览器: {session.BrowserName}");
                logEntry.AppendLine($"开始时间: {session.StartTime:HH:mm:ss}");

                string endTime;
                if (session.EndTime == DateTime.MinValue)
                {
                    endTime = "运行中";
                }
                else
                {
                    endTime = session.EndTime.ToString("HH:mm:ss");
                }
                logEntry.AppendLine($"结束时间: {endTime}");

                if (session.EndTime != DateTime.MinValue)
                {
                    var duration = session.EndTime - session.StartTime;
                    logEntry.AppendLine($"总持续时间: {duration:hh\\:mm\\:ss}");
                }

                if (session.Urls.Any())
                {
                    // 过滤掉不需要显示的访问记录
                    var validUrls = session.Urls
                        .Where(u => u.EndTime != DateTime.MinValue)
                        .Where(u => ShouldLogUrlVisit(u))
                        .ToList();

                    if (validUrls.Any())
                    {
                        logEntry.AppendLine("访问记录:");
                        foreach (var urlVisit in validUrls)
                        {
                            logEntry.AppendLine($"  URL: {urlVisit.Url}");
                            logEntry.AppendLine($"    标题: {urlVisit.Title}");
                            logEntry.AppendLine($"    开始: {urlVisit.StartTime:HH:mm:ss}");
                            logEntry.AppendLine($"    结束: {urlVisit.EndTime:HH:mm:ss}");
                            var duration = urlVisit.EndTime - urlVisit.StartTime;
                            logEntry.AppendLine($"    时长: {duration:hh\\:mm\\:ss}");
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
                // 只记录 UrlOpened 事件
                if (eventType != "UrlOpened")
                {
                    return;
                }

                var logEntry = new StringBuilder();
                logEntry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");
                logEntry.AppendLine($"事件: {eventType}");
                logEntry.AppendLine($"浏览器: {browserName}");
                logEntry.AppendLine($"URL: {urlVisit.Url}");
                logEntry.AppendLine($"标题: {urlVisit.Title}");
                logEntry.AppendLine($"开始时间: {urlVisit.StartTime:HH:mm:ss}");
                logEntry.AppendLine($"结束时间: 访问中");
                logEntry.AppendLine(new string('-', 40));

                WriteToLog("Browser", logEntry.ToString());
            }
            catch (Exception ex)
            {
                WriteDebugLog($"记录URL访问失败: {ex.Message}");
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
        public string Title { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public IntPtr WindowHandle { get; set; }
    }

    public class WindowInfo
    {
        public IntPtr HWnd { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
    }
}