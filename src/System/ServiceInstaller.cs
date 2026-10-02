using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;

namespace LiteMonitor.src.SystemServices
{
    public static class ServiceInstaller
    {
        public const string ServiceName = "LiteMonitorSensorService";

        public static bool IsInstalled()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                var _ = sc.Status;
                return true;
            }
            catch { return false; }
        }

        public static bool IsRunning()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        /// <summary>
        /// 启动服务（不弹 UAC，服务已在系统里，Start 不需要管理员）
        /// </summary>
        private static bool TryStartService()
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status == ServiceControllerStatus.Running) return true;
                if (sc.Status == ServiceControllerStatus.StartPending)
                {
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    return sc.Status == ServiceControllerStatus.Running;
                }

                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        /// <summary>
        /// 确保服务已安装且正在运行
        /// - 已安装 + 运行中 → 直接返回
        /// - 已安装 + 停止 → 尝试启动（不需要 UAC）
        /// - 未安装 → 弹一次 UAC 安装并启动
        /// </summary>
        public static void EnsureInstalled()
        {
            // 1) 已安装：检查是否在运行
            if (IsInstalled())
            {
                if (IsRunning()) return;

                // 尝试启动（用户态 Start，不需要 UAC）
                for (int i = 0; i < 3; i++)
                {
                    if (TryStartService()) return;
                    Thread.Sleep(500);
                }

                // 三次启动失败 → 服务可能坏了，删除重装
                try
                {
                    using var sc = new ServiceController(ServiceName);
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                }
                catch { }

                TryDeleteServiceWithUac();
            }

            // 2) 未安装（或刚被删）：弹 UAC 安装
            string exePath = Process.GetCurrentProcess().MainModule!.FileName;

            string ps =
                "$ErrorActionPreference='Stop';" +
                $"sc.exe create {ServiceName} binPath= '\"{exePath}\" --service' start= auto DisplayName= 'LiteMonitor Sensor Service' | Out-Null;" +
                $"Start-Service -Name {ServiceName} -ErrorAction SilentlyContinue;";

            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                Process.Start(psi)?.WaitForExit(30000);

                // 等 3 秒让服务启动
                Thread.Sleep(3000);
            }
            catch
            {
                // 用户点了"否"，忽略。主程序会退回无温度模式。
            }
        }

        /// <summary>
        /// 用 UAC 权限删除损坏的服务
        /// </summary>
        private static void TryDeleteServiceWithUac()
        {
            string ps =
                "$ErrorActionPreference='SilentlyContinue';" +
                $"sc.exe stop {ServiceName};" +
                $"Start-Sleep 2;" +
                $"sc.exe delete {ServiceName};";

            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                Process.Start(psi)?.WaitForExit(15000);
                Thread.Sleep(1000);
            }
            catch { }
        }
    }
}