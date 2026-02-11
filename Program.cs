using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace HiJk
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // 设置未处理异常处理
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.ThreadException += Application_ThreadException;

            // 启用应用程序的视觉样式
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                // 检查是否已存在实例（防止重复运行）
                if (IsAlreadyRunning())
                {
                    MessageBox.Show("HiJk 系统监控已经在运行中！", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 设置进程优先级
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

                // 运行主窗体
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                HandleStartupError(ex);
            }
        }

        private static bool IsAlreadyRunning()
        {
            string processName = Process.GetCurrentProcess().ProcessName;
            Process[] processes = Process.GetProcessesByName(processName);
            return processes.Length > 1;
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                LogError("未处理的应用程序域异常", ex);
                ShowErrorDialog("系统错误", ex);
            }
        }

        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            LogError("未处理的线程异常", e.Exception);
            ShowErrorDialog("应用程序错误", e.Exception);
        }

        private static void HandleStartupError(Exception ex)
        {
            LogError("应用程序启动失败", ex);

            string errorMessage = $"应用程序启动失败：{ex.Message}\n\n" +
                                 $"请尝试以管理员身份运行程序。";

            MessageBox.Show(errorMessage, "启动错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

            // 等待用户查看错误信息
            System.Threading.Thread.Sleep(1000);
        }

        private static void LogError(string message, Exception ex)
        {
            try
            {
                string logDir = "Logs";
                if (!System.IO.Directory.Exists(logDir))
                {
                    System.IO.Directory.CreateDirectory(logDir);
                }

                string logFile = System.IO.Path.Combine(logDir, "Error.log");
                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n" +
                                   $"错误：{message}\n" +
                                   $"异常：{ex.GetType().Name}\n" +
                                   $"消息：{ex.Message}\n" +
                                   $"堆栈：{ex.StackTrace}\n" +
                                   new string('-', 60) + "\n";

                System.IO.File.AppendAllText(logFile, logContent, System.Text.Encoding.UTF8);
            }
            catch
            {
                // 忽略日志记录错误
            }
        }

        private static void ShowErrorDialog(string title, Exception ex)
        {
            string errorMessage = $"{ex.Message}\n\n" +
                                 $"异常类型：{ex.GetType().Name}\n" +
                                 $"详细错误信息已记录到 Error.log 文件中。";

            if (ex.InnerException != null)
            {
                errorMessage += $"\n\n内部异常：{ex.InnerException.Message}";
            }

            MessageBox.Show(errorMessage, title,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}