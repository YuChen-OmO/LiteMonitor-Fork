using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json; // 如果报错，请引用 System.Text.Json NuGet包，或改用 Newtonsoft

namespace LiteMonitor.src.Core
{
    // === 数据模型 ===
    public class TrafficData
    {
        // Key格式: "2023-10-27"
        public Dictionary<string, DailyRecord> History { get; set; } = new Dictionary<string, DailyRecord>();
    }

    public class DailyRecord
    {
        public long Upload { get; set; }
        public long Download { get; set; }
    }

    // === 管理器 (静态单例) ===
    public static class TrafficLogger
    {
        private static readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "TrafficHistory.json");
        private static readonly string _backupPath = Path.Combine(AppContext.BaseDirectory, "TrafficHistory.json.bak");
        private static readonly string _tempPath = Path.Combine(AppContext.BaseDirectory, "TrafficHistory.json.tmp");
        private static readonly object _ioLock = new object(); // 文件锁
        private static readonly object _dataLock = new object(); // 数据锁
        // ====== 新增缓存字段 ======
        private static DateTime _cachedDate;
        private static string _cachedDateKey = "";

        public static TrafficData Data { get; private set; } = new TrafficData();

        // 启动时加载
        public static void Load()
        {
            lock (_ioLock)
            {
                TrafficData loaded = TryLoadFile(_filePath) ?? TryLoadFile(_backupPath) ?? new TrafficData();
                loaded.History ??= new Dictionary<string, DailyRecord>();
                lock (_dataLock)
                {
                    Data = loaded;
                }
            }
        }

        private static TrafficData? TryLoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<TrafficData>(json) ?? new TrafficData();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrafficLogger] 读取历史流量失败: {path}, {ex.Message}");
                return null;
            }
        }

        private static TrafficData CloneDataUnsafe()
        {
            Data.History ??= new Dictionary<string, DailyRecord>();
            return new TrafficData
            {
                History = Data.History.ToDictionary(
                    kv => kv.Key,
                    kv => new DailyRecord
                    {
                        Upload = kv.Value?.Upload ?? 0,
                        Download = kv.Value?.Download ?? 0
                    })
            };
        }

        private static void AtomicWrite(string json)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_tempPath, json);

            if (File.Exists(_filePath))
            {
                try { File.Copy(_filePath, _backupPath, true); }
                catch (Exception ex) { Debug.WriteLine($"[TrafficLogger] 写入备份失败: {ex.Message}"); }
            }

            File.Move(_tempPath, _filePath, true);
        }

        private static void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrafficLogger] 清理临时文件失败: {ex.Message}");
            }
        }

        private static string SerializeSnapshot()
        {
            var opt = new JsonSerializerOptions { WriteIndented = true };
            lock (_dataLock)
            {
                return JsonSerializer.Serialize(CloneDataUnsafe(), opt);
            }
        }

        private static DailyRecord GetOrCreateTodayRecordUnsafe(string key)
        {
            Data.History ??= new Dictionary<string, DailyRecord>();
            if (!Data.History.TryGetValue(key, out var record) || record == null)
            {
                record = new DailyRecord();
                Data.History[key] = record;
            }
            return record;
        }

        private static string GetTodayKey()
        {
            if (DateTime.Today != _cachedDate)
            {
                _cachedDate = DateTime.Today;
                _cachedDateKey = UIUtils.Intern(_cachedDate.ToString("yyyy-MM-dd"));
            }
            return _cachedDateKey;
        }

        private static void SaveInternal()
        {
            lock (_ioLock)
            {
                try
                {
                    string json = SerializeSnapshot();
                    AtomicWrite(json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TrafficLogger] 保存历史流量失败: {ex.Message}");
                    CleanupTempFile();
                }
            }
        }

        // 关闭时保存
        public static void Save()
        {
            SaveInternal();
        }

        // 核心：累加数据
        public static void AddTraffic(long upBytes, long downBytes)
        {
            string key = GetTodayKey();

            lock (_dataLock)
            {
                var record = GetOrCreateTodayRecordUnsafe(key);
                record.Upload += upBytes;
                record.Download += downBytes;
            }
        }

        // 获取今日数据 (供 UI 显示)
        public static (long up, long down) GetTodayStats()
        {
            string key = GetTodayKey();
            lock (_dataLock)
            {
                Data.History ??= new Dictionary<string, DailyRecord>();
                if (Data.History.TryGetValue(key, out var rec))
                {
                    return (rec.Upload, rec.Download);
                }
            }
            return (0, 0);
        }

        // [新增] 删除指定日期的记录
        public static void RemoveRecord(string dateKey)
        {
            bool changed;
            lock (_dataLock)
            {
                Data.History ??= new Dictionary<string, DailyRecord>();
                changed = Data.History.Remove(dateKey);
            }

            if (changed) Save(); // 立即保存更改
        }

        // [新增] 清空所有历史
        public static void ClearHistory()
        {
            lock (_dataLock)
            {
                Data.History ??= new Dictionary<string, DailyRecord>();
                Data.History.Clear();
            }
            Save();
        }
    }
}
