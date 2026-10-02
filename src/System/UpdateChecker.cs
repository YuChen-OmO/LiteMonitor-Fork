using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;
using System.Net.Security;
using LiteMonitor.src.Core;

using System.IO;
using System.IO.Compression;

namespace LiteMonitor
{
    /// <summary>
    /// LiteMonitor Fork 版自动更新模块
    /// - version.json 从 Fork 仓库读取
    /// - ZIP 下载从 Fork 仓库 Releases 读取
    /// - 首次运行安装服务，之后免 UAC
    /// - CheckAsync() 可被右键菜单直接调用
    /// </summary>
    public static class UpdateChecker
    {
        // Fork 仓库信息（★ 改成 public，供 MenuManager 引用）
        public const string ForkRepoOwner = "YuChen-OmO";
        public const string ForkRepoName = "LiteMonitor-Fork";
        public const string ForkRepoUrl = "https://github.com/YuChen-OmO/LiteMonitor-Fork";
        public const string ForkIssuesUrl = "https://github.com/YuChen-OmO/LiteMonitor-Fork/issues";

        // 全局 HttpClient（降低系统资源消耗）
        private static readonly HttpClient http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            }
        })
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        // ========================================================
        // 【1】version.json 源（指向 Fork 仓库）
        // ========================================================
        private static readonly string[] VersionJsonUrls =
        {
            // Fork 仓库 GitHub RAW（主源）
            $"https://raw.githubusercontent.com/{ForkRepoOwner}/{ForkRepoName}/master/resources/version.json",

            // Fork 仓库 GitHub RAW（main 分支备选）
            $"https://raw.githubusercontent.com/{ForkRepoOwner}/{ForkRepoName}/main/resources/version.json",
        };

        // ========================================================
        // 【2】ZIP 下载镜像（指向 Fork Releases）
        // ========================================================
        private static readonly string[] Mirrors =
        {
            // Fork 仓库 GitHub Releases
            $"https://github.com/{ForkRepoOwner}/{ForkRepoName}/releases/download/v{{0}}/LiteMonitor_Fork_v{{0}}-win-x64.zip",
        };

        /// <summary>
        /// 缓存最新版本信息，供菜单等处使用
        /// </summary>
        public static (string latest, string changelog, string releaseDate)? LatestVersionInfo { get; private set; }

        /// <summary>
        /// 是否发现了新版本
        /// </summary>
        public static bool IsUpdateFound => LatestVersionInfo != null;

        // ========================================================
        // 【3】主入口：检查更新
        // ========================================================
        public static async Task CheckAsync(bool showMessage = false)
        {
            try
            {
                var info = await GetVersionInfo();
                if (info == null)
                {
                    if (showMessage)
                    {
                        if (LanguageManager.CurrentLang == "zh")
                        {
                            MessageBox.Show("无法连接到更新服务器，请稍后重试。",
                                "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            MessageBox.Show("Unable to connect to update server, please try again later.",
                                "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    return;
                }

                string latest = info.Value.latest;
                string changelog = info.Value.changelog;
                string releaseDate = info.Value.releaseDate;
                string current = GetCurrentVersion();

                if (new Version(latest) > new Version(current))
                {
                    LatestVersionInfo = info;

                    var settings = Settings.Load();
                    if (!showMessage && !settings.AutoCheckUpdate) return;

                    var sortedUrls = await GetSortedZipUrls(latest);

                    bool isZh = settings?.Language?.ToLower() == "zh";

                    // 更新窗口显示 Fork 版名称和链接
                    var context = new DownloadContext
                    {
                        Title = isZh ? "发现新版本！" : "New Version!",
                        VersionLabel = $"⚡️LiteMonitor-Fork_v{latest}",
                        Description = isZh
                            ? $"更新日志：\n{changelog}\n更新日期：\n{releaseDate}\n\n项目主页：{ForkRepoUrl}\n问题反馈：{ForkIssuesUrl}"
                            : $"Changelog:\n{changelog}\nRelease date:\n{releaseDate}\n\nProject: {ForkRepoUrl}\nIssues: {ForkIssuesUrl}",
                        Urls = sortedUrls.ToArray(),
                        SavePath = Path.Combine(AppContext.BaseDirectory, "resources", "update.zip"),
                        ActionButtonText = "Update",
                        AutoExitOnSuccess = true
                    };

                    new UpdateDialog(context, settings).ShowDialog();
                }
                else
                {
                    LatestVersionInfo = null;

                    if (showMessage)
                    {
                        if (LanguageManager.CurrentLang == "zh")
                        {
                            MessageBox.Show($"当前已是最新版本 ：v{current}\n发布日期：{releaseDate}", "检查更新",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show($"Already the latest version: v{current}\nRelease date: {releaseDate}", "Update Check",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdateChecker] Error: " + ex.Message);
                if (showMessage)
                {
                    if (LanguageManager.CurrentLang == "zh")
                    {
                        MessageBox.Show("检查更新失败，可能是网络问题。",
                            "检查更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    else
                    {
                        MessageBox.Show("Update check failed, possibly due to network issues.",
                            "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        // ========================================================
        // 【4】version.json 自动 fallback
        // ========================================================
        private static async Task<(string latest, string changelog, string releaseDate)?> GetVersionInfo()
        {
            foreach (var url in VersionJsonUrls)
            {
                try
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(3000);

                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    var task = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    var finished = await Task.WhenAny(task, Task.Delay(3000, cts.Token));

                    if (finished != task)
                        throw new TimeoutException("Connection timeout");

                    var resp = await task;

                    if (!resp.IsSuccessStatusCode)
                        throw new Exception("Bad status");

                    string json = await resp.Content.ReadAsStringAsync(cts.Token);

                    var doc = JsonDocument.Parse(json);

                    string latest = doc.RootElement.GetProperty("version").GetString()!;
                    string log = doc.RootElement.GetProperty("changelog").GetString()!;
                    string releaseDate = doc.RootElement.GetProperty("releaseDate").GetString()!;

                    return (latest, log, releaseDate);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Update] 源失败：{url} -> {ex.Message}");
                    continue;
                }
            }

            return null;
        }

        // ========================================================
        // 【5】测速获取排序后的 ZIP 下载源
        // ========================================================
        private static async Task<List<string>> GetSortedZipUrls(string version)
        {
            var tests = new Task<(string url, long speed)>[Mirrors.Length];

            for (int i = 0; i < Mirrors.Length; i++)
            {
                string url = string.Format(Mirrors[i], version);
                tests[i] = TestMirrorSpeed(url);
            }

            var results = await Task.WhenAll(tests);

            var sorted = results
                .OrderByDescending(r => r.speed)
                .Select(r => r.url)
                .ToList();

            if (sorted.Count == 0 || results.All(r => r.speed == 0))
            {
                return Mirrors.Select(m => string.Format(m, version)).ToList();
            }

            return sorted;
        }

        // ========================================================
        // 【6】轻量测速（读取 32KB 换算下载速度）
        // ========================================================
        private static async Task<(string url, long speed)> TestMirrorSpeed(string url)
        {
            try
            {
                var sw = Stopwatch.StartNew();

                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                    return (url, 0);

                using var stream = await resp.Content.ReadAsStreamAsync();

                byte[] testBuf = new byte[32 * 1024];
                int read = await stream.ReadAsync(testBuf, 0, testBuf.Length);

                sw.Stop();

                if (read <= 0)
                    return (url, 0);

                long speed = (long)(read * 1000.0 / Math.Max(sw.ElapsedMilliseconds, 1));

                return (url, speed);
            }
            catch
            {
                return (url, 0);
            }
        }

        // ========================================================
        // 【7】获取当前版本号
        // ========================================================
        public static string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (string.IsNullOrWhiteSpace(version))
                version = Application.ProductVersion;

            int plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
                version = version.Substring(0, plusIndex);

            return version;
        }

        // ========================================================
        // 【8】主程序抢先更新 Updater.exe (解决自更新死锁)
        // ========================================================
        public static string? PreUpdateUpdater(string zipPath)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string resourcesDir = Path.Combine(baseDir, "resources");

                if (!Directory.Exists(resourcesDir)) Directory.CreateDirectory(resourcesDir);

                string[] updaterNames = { "Updater", "LiteMonitor.Updater" };
                foreach (var name in updaterNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (p.MainModule != null &&
                                p.MainModule.FileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Kill();
                            }
                        }
                        catch { }
                    }
                }

                System.Threading.Thread.Sleep(200);

                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var entry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("LiteMonitor.Updater.exe", StringComparison.OrdinalIgnoreCase));

                    if (entry == null)
                    {
                        entry = archive.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase));
                    }

                    if (entry != null)
                    {
                        string fileName = Path.GetFileName(entry.FullName);
                        string targetPath = Path.Combine(resourcesDir, fileName);

                        entry.ExtractToFile(targetPath, true);

                        Debug.WriteLine($"[UpdateChecker] Updater 预更新成功: {targetPath}");

                        return targetPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateChecker] Updater 预更新失败: {ex.Message}");
            }

            return null;
        }
    }
}