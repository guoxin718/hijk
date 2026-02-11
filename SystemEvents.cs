using System;
using System.Diagnostics;
using System.Management;

namespace HiJk
{
    public class SystemEvents : IDisposable
    {
        private ManagementEventWatcher processStartWatcher;
        private ManagementEventWatcher processStopWatcher;
        private bool isSubscribed = false;
        private bool disposed = false;

        public event Action<ApplicationInfo> ApplicationOpened;
        public event Action<ApplicationInfo> ApplicationClosed;

        public void Subscribe()
        {
            if (isSubscribed) return;

            try
            {
                // 监听进程创建事件
                var startQuery = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
                processStartWatcher = new ManagementEventWatcher(startQuery);
                processStartWatcher.EventArrived += OnProcessStart;
                processStartWatcher.Start();

                // 监听进程结束事件
                var stopQuery = new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace");
                processStopWatcher = new ManagementEventWatcher(stopQuery);
                processStopWatcher.EventArrived += OnProcessStop;
                processStopWatcher.Start();

                isSubscribed = true;
                Debug.WriteLine("系统事件监听已启动");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动事件监听失败: {ex.Message}");
            }
        }

        public void Unsubscribe()
        {
            if (!isSubscribed) return;

            try
            {
                processStartWatcher?.Stop();
                processStopWatcher?.Stop();
                processStartWatcher?.Dispose();
                processStopWatcher?.Dispose();

                processStartWatcher = null;
                processStopWatcher = null;
                isSubscribed = false;
                Debug.WriteLine("系统事件监听已停止");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止事件监听失败: {ex.Message}");
            }
        }

        private void OnProcessStart(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var processId = Convert.ToUInt32(e.NewEvent.Properties["ProcessID"].Value);
                var processName = e.NewEvent.Properties["ProcessName"].Value.ToString();

                var appInfo = new ApplicationInfo
                {
                    ProcessName = processName,
                    ProcessId = processId,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.MinValue,
                    FilePath = GetProcessPath(processId)
                };

                ApplicationOpened?.Invoke(appInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理进程启动事件失败: {ex.Message}");
            }
        }

        private void OnProcessStop(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var processId = Convert.ToUInt32(e.NewEvent.Properties["ProcessID"].Value);
                var processName = e.NewEvent.Properties["ProcessName"].Value.ToString();

                var appInfo = new ApplicationInfo
                {
                    ProcessName = processName,
                    ProcessId = processId,
                    EndTime = DateTime.Now
                };

                ApplicationClosed?.Invoke(appInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理进程停止事件失败: {ex.Message}");
            }
        }

        private string GetProcessPath(uint processId)
        {
            try
            {
                var query = $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {processId}";
                using var searcher = new ManagementObjectSearcher(query);
                foreach (ManagementObject obj in searcher.Get())
                {
                    return obj["ExecutablePath"]?.ToString() ?? string.Empty;
                }
            }
            catch
            {
                // 忽略错误
            }
            return string.Empty;
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
                    Unsubscribe();
                }
                disposed = true;
            }
        }

        ~SystemEvents()
        {
            Dispose(false);
        }
    }
}