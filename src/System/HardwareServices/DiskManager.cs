using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    public class DiskManager
    {
        // ★★★ [新增] 2. 记录每个硬盘最后一次活跃的时间 (用于判断是否深睡)
        private Dictionary<IHardware, DateTime> _diskLastActiveTime = new Dictionary<IHardware, DateTime>();
        // 磁盘智能缓存
        private IHardware? _cachedDiskHw;
        private DateTime _lastDiskScan = DateTime.MinValue;
        // ★★★ [新增] 静态缓存：避免 DriveInfo 重复创建 ★★★
        private static readonly Dictionary<string, DriveInfo> _driveInfoCache = new();

        public void ClearCache()
        {
            _diskLastActiveTime.Clear();
            _cachedDiskHw = null;
            // 可以选择清理 DriveInfo 缓存，也可以保留（因为它通常不变）
            _driveInfoCache.Clear();
        }

        // ===========================================================
        // 更新逻辑 (原 UpdateAll 中的部分)
        // ===========================================================
        public void ProcessUpdate(IHardware hw, Settings cfg, bool isSlowScanTick, bool needDiskBgScan)
        {
            // 1. 严格遵守首选磁盘锁定
            if (!string.IsNullOrEmpty(cfg.PreferredDisk) && 
                !hw.Name.Equals(cfg.PreferredDisk, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 初始化活跃时间记录
            if (!_diskLastActiveTime.ContainsKey(hw)) _diskLastActiveTime[hw] = DateTime.Now;

            // 判断是否是当前 UI 上显示的那个盘
            bool isTarget = (_cachedDiskHw != null && hw == _cachedDiskHw) ||
                            (hw.Name == cfg.LastAutoDisk) ||
                            (hw.Name == cfg.PreferredDisk);
            
            bool shouldUpdate = false;
            double idleMinutes = (DateTime.Now - _diskLastActiveTime[hw]).TotalMinutes;

            // === 🧠 智能退避核心逻辑 ===
            if (isTarget)
            {
                shouldUpdate = true;
            }
            else
            {
                // B. 如果是后台盘（比如你的 E 盘）：
                if (idleMinutes > 5) shouldUpdate = false; // [💤 深睡模式]
                else if (idleMinutes > 1) { if (needDiskBgScan) shouldUpdate = true; } // [❄️ 冷却模式]
                else { if (isSlowScanTick) shouldUpdate = true; } // [🔥 活跃模式]
            }

            // 执行更新
            if (shouldUpdate)
            {
                hw.Update();
                // ★★★ 检查是否有流量，如果有，重置活跃计时器 ★★★
                bool hasTraffic = false;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Throughput && s.Value.HasValue && s.Value.Value > 1024) // > 1KB/s
                    {
                        hasTraffic = true;
                        break;
                    }
                }
                if (hasTraffic) _diskLastActiveTime[hw] = DateTime.Now;
            }
        }

        // ===========================================================
        // 获取最佳磁盘数值 (已简化：Preferred 逻辑已移至 ValueProvider 静态缓存)
        // ===========================================================
        public float? GetBestValue(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            // 1. 运行时缓存 (针对自动选择模式)
            if (_cachedDiskHw != null)
            {
                // ★★★ 【修复 2】存活检查：防止持有僵尸对象的引用 ★★★
                if (!computer.Hardware.Contains(_cachedDiskHw))
                {
                    _cachedDiskHw = null;
                }
                else
                {
                    float? cachedVal = ReadDiskSensor(_cachedDiskHw, key, lastValidMap, syncLock);
                    // 有读写活动或冷却期内，直接返回
                    // ★★★ [新增] 温度支持 ★★★
                    if ((cachedVal.HasValue && cachedVal.Value > 0.1f) || key.Contains("Temp") || (DateTime.Now - _lastDiskScan).TotalSeconds < 10)
                        return cachedVal;
                }
            }
            // === [核心修复] 逻辑分区处理：使用缓存的 DriveInfo ===
            // key 格式如: DISK.C.Used
            var parts = key.Split('.');
            if (parts.Length >= 3 && parts[1].Length == 1 && char.IsLetter(parts[1][0]))
            {
                string driveLetter = parts[1]; // "C"
                string type = parts[2];        // "Used"
                string root = driveLetter + ":\\";

                DriveInfo di;
                // ★★★ 优先查缓存 ★★★
                if (!_driveInfoCache.TryGetValue(root, out di))
                {
                    try 
                    { 
                        di = new DriveInfo(root); 
                        _driveInfoCache[root] = di; // 存入缓存
                    } 
                    catch { return null; }
                }

                if (type == "Used") 
                {
                    try 
                    { 
                        // IsReady 不会产生 Volume 字符串垃圾
                        if (di.IsReady) return (100f - (float)((double)di.TotalFreeSpace / di.TotalSize * 100.0));
                    } 
                    catch { return 0f; }
                }
            }
            // ★★★ [新增] B. 尝试启动时缓存 (Settings 记忆) ★★★
            if (_cachedDiskHw == null && !string.IsNullOrEmpty(cfg.LastAutoDisk))
            {
                var savedHw = computer.Hardware.FirstOrDefault(h => h.Name == cfg.LastAutoDisk);
                if (savedHw != null)
                {
                    _cachedDiskHw = savedHw;
                    _lastDiskScan = DateTime.Now;
                    return ReadDiskSensor(savedHw, key, lastValidMap, syncLock);
                }
            }

            // C. 全盘扫描
            string sysPrefix = "";
            try { sysPrefix = Path.GetPathRoot(Environment.SystemDirectory)?.Substring(0, 2) ?? ""; } catch { }

            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
            {
                bool isSystem = !string.IsNullOrEmpty(sysPrefix) && (SensorMap.Has(hw.Name, sysPrefix) || hw.Sensors.Any(s => SensorMap.Has(s.Name, sysPrefix)));
                ISensor? read = null, write = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (SensorMap.Has(s.Name, "read")) read ??= s;
                    if (SensorMap.Has(s.Name, "write")) write ??= s;
                }
                if (read == null && write == null) continue;

                double score = (read?.Value ?? 0) + (write?.Value ?? 0);
                if (isSystem) score += 1e9; // 系统盘优先

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = (key == "DISK.Read") ? read : write;
                }
            }

            // D. 更新缓存
            if (bestHw != null)
            {
                _cachedDiskHw = bestHw;
                _lastDiskScan = DateTime.Now;
                
                // ★★★ [新增] 记住这次的选择 ★★★
                if (cfg.LastAutoDisk != bestHw.Name)
                {
                    cfg.LastAutoDisk = bestHw.Name;
                }
            }

            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            
            // ★★★ [新增] 温度支持补漏 ★★★
            if (key == "DISK.Temp" && bestHw != null) return ReadDiskSensor(bestHw, key, lastValidMap, syncLock);

            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        public static ISensor? FindBestTempSensor(IHardware hw)
        {
            ISensor? best = null;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Temperature)
                {
                    // 排除干扰项
                    if (s.Name.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.Name.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (s.Name == "Temperature") return s;
                    if (best == null) best = s;
                }
            }
            return best;
        }

        private float? ReadDiskSensor(IHardware hw, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            // ★★★ [新增] 温度支持 ★★★
            if (key == "DISK.Temp")
            {
                var best = FindBestTempSensor(hw);
                if (best != null) return SafeRead(best, key, lastValidMap, syncLock);
                return null;
            }

            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "DISK.Read" && SensorMap.Has(s.Name, "read")) return SafeRead(s, key, lastValidMap, syncLock);
                if (key == "DISK.Write" && SensorMap.Has(s.Name, "write")) return SafeRead(s, key, lastValidMap, syncLock);
            }
            return SafeRead(null, key, lastValidMap, syncLock);
        }

        private float? SafeRead(ISensor? s, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            if (s?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }
    }
}