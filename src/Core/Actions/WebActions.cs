using System;
using System.Diagnostics;
using LiteMonitor.src.WebServer;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.Core.Actions
{
    public static class WebActions
    {
        public static void OpenWebMonitor(Settings cfg)
        {
            // 不再阻断，只尝试获取运行端口，如果没运行则使用配置端口
            int port = 0;
            
            if (LiteWebServer.Instance != null && LiteWebServer.Instance.IsRunning)
            {
                port = LiteWebServer.Instance.CurrentRunningPort;
            }

            // 如果服务没运行或端口无效，使用配置端口
            if (port <= 0 && cfg != null)
            {
                port = cfg.WebServerPort;
            }

            if (port > 0)
            {
                string host = "localhost";
                // 优先使用内网IP
                if (HardwareMonitor.Instance != null)
                {
                    string ip = HardwareMonitor.Instance.GetNetworkIP();
                    if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && ip != "127.0.0.1") 
                    {
                        host = ip;
                    }
                }

                string url = $"http://{host}:{port}";
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    // 如果使用 IP 打开失败，尝试回退到 localhost
                    if (host != "localhost")
                    {
                         try 
                         {
                             Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true });
                         }
                         catch { }
                    }
                    Debug.WriteLine("Failed to open web monitor: " + ex.Message);
                }
            }
        }
    }
}
