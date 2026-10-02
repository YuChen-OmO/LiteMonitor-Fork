using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor
{
    internal static class Program
    {
        private static Mutex? _mutex = null;
        private const string ClientLogPath = @"C:\LiteMonitor_Client.log";

        [STAThread]
        static void Main(string[] args)
        {
            // =================================================================
            // 模式 1：服务模式（由 SCM 以 --service 参数启动）
            // =================================================================
            if (args.Contains("--service"))
            {
                System.ServiceProcess.ServiceBase.Run(
                    new LiteMonitor.src.SystemServices.SensorService());
                return;
            }

            // =================================================================
            // 模式 2：只装服务（命令行 --install-service）
            // =================================================================
            if (args.Contains("--install-service"))
            {
                try
                {
                    ServiceInstaller.EnsureInstalled();
                    LogClient("Manual service install completed");
                }
                catch (Exception ex)
                {
                    LogClient($"Manual install FAILED: {ex.Message}");
                }
                return;
            }

            // =================================================================
            // 模式 3：正常 GUI 启动
            // =================================================================

            // 首次运行：装服务，弹一次 UAC
            try
            {
                ServiceInstaller.EnsureInstalled();
            }
            catch (Exception ex)
            {
                LogClient($"EnsureInstalled exception: {ex.Message}");
            }

            // 记录服务状态（诊断用）
            try
            {
                bool installed = ServiceInstaller.IsInstalled();
                bool running = ServiceInstaller.IsRunning();
                LogClient($"Service state: installed={installed}, running={running}");
            }
            catch { }

            // 单实例锁
            bool createNew;
            string mutexName;
            try
            {
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                }
                else
                {
                    string appFolderPath = Path.GetDirectoryName(exePath);
                    string sanitizedPath = appFolderPath?.ToLower()
                                                         .Replace('\\', '_')
                                                         .Replace(':', '_')
                                                         .Replace('/', '_')
                                                         .Replace(' ', '_');

                    string baseName = $"Global\\LiteMonitor_SingleInstance_{sanitizedPath}_Mutex";
                    if (baseName.Length > 250)
                        baseName = $"Global\\LiteMonitor_SingleInstance_{sanitizedPath.GetHashCode()}_Mutex";

                    mutexName = baseName;
                }

                _mutex = new Mutex(true, mutexName, out createNew);
            }
            catch
            {
                mutexName = "Global\\LiteMonitor_SingleInstance_Mutex_UniqueKey";
                _mutex = new Mutex(true, mutexName, out createNew);
            }

            if (!createNew) return;

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm());
            }
            finally
            {
                try { FpsCounter.ForceKillZombies(); } catch { }

                if (_mutex != null)
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static void LogClient(string msg)
        {
            try
            {
                File.AppendAllText(ClientLogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            }
            catch { }
        }

        static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            LogCrash(e.Exception, "UI_Thread");
        }

        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash(e.ExceptionObject as Exception, "Background_Thread");
        }

        static void LogCrash(Exception? ex, string source)
        {
            if (ex == null) return;

            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "LiteMonitor_Error.log");

                string errorMsg = "==================================================\n" +
                                  $"[Time]: {DateTime.Now}\n" +
                                  $"[Source]: {source}\n" +
                                  $"[Message]: {ex.Message}\n" +
                                  $"[Stack]:\n{ex.StackTrace}\n" +
                                  "==================================================\n\n";

                File.AppendAllText(logPath, errorMsg);

                MessageBox.Show($"程序遇到致命错误！\n错误日志已保存至：{logPath}\n\n原因：{ex.Message}",
                                "LiteMonitor Crash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}