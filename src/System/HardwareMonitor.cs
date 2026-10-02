using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    public sealed class HardwareMonitor : IDisposable
    {
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;

        private readonly Settings _cfg;
        private readonly object _lock = new object();
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly FpsCounter _fpsCounter;
        private readonly DriverInstaller _driverInstaller;
        private readonly HardwareValueProvider _valueProvider;
        private readonly PerformanceCounterManager _perfCounterManager;
        private static readonly Dictionary<string, float> _lastValidMap = new();

        private readonly Computer _computer;

        private volatile bool _isOpening = false;
        private long _secondsCounter = 0;
        private double _secondAccumulator = 0;
        private DateTime _lastTrafficTime = DateTime.Now;

        private static int _readCount = 0;
        private const string ClientLogPath = @"C:\LiteMonitor_Client.log";

        private static List<HardwareItem> _hardwareList = new();
        private static float? _cpuTempFromService = null;
        private static float? _gpuTempFromService = null;
        private static string _cpuTempSource = "";
        private static string _gpuTempSource = "";

        private static readonly object _pipeLock = new object();

        public class HardwareItem
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public List<SensorItem> Sensors { get; set; } = new();
        }

        public class SensorItem
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
        }

        public object SyncLock => _lock;
        public IComputer ComputerInstance => _computer;

        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            _computer = new Computer();

            _perfCounterManager = new PerformanceCounterManager();
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager(_perfCounterManager);
            _diskManager = new DiskManager();
            _driverInstaller = new DriverInstaller(cfg, () => { }, () => { });
            _fpsCounter = new FpsCounter(_driverInstaller);

            _valueProvider = new HardwareValueProvider(
                _computer, cfg, _sensorMap, _networkManager, _diskManager,
                _fpsCounter, _perfCounterManager, _lock, _lastValidMap);

            Task.Run(() =>
            {
                _isOpening = true;
                try { _perfCounterManager.InitializeAsync(); } catch { }
                _isOpening = false;

                // ★ 启动后立刻拉一次硬件列表
                try { ForceRefreshHardwareList(); } catch { }
            });
        }

        public float? Get(string key)
        {
            if (key == "CPU.Temp" && _cpuTempFromService.HasValue && _cpuTempFromService.Value > 0)
                return _cpuTempFromService;
            if (key == "GPU.Temp" && _gpuTempFromService.HasValue && _gpuTempFromService.Value > 0)
                return _gpuTempFromService;

            return _isOpening ? _valueProvider.GetStartupValue(key) : _valueProvider.GetValue(key);
        }

        public string GetCpuTempSource() => _cpuTempSource;
        public string GetGpuTempSource() => _gpuTempSource;

        public string GetNetworkIP() => _networkManager.GetCurrentIP();
        public Task SmartCheckDriver() => Task.CompletedTask;
        public void RefreshHardwareConfig() { }

        public void UpdateAll()
        {
            try
            {
                UpdateTiming();
                _valueProvider.OnUpdateTickStarted();

                var snapshot = ReadFromService();
                if (snapshot.Count > 0)
                {
                    _valueProvider.UpdateFromSnapshot(snapshot);
                }

                SystemOptimizer.RunMaintenanceTasks(_secondsCounter);
                OnValuesUpdated?.Invoke();
            }
            catch { }
        }

        public void CleanMemory(Action<int>? onProgress = null) => SystemOptimizer.CleanMemory(onProgress);

        public void Dispose()
        {
            _valueProvider.Dispose();
            _perfCounterManager.Dispose();
            _fpsCounter.Dispose();
            _networkManager.ClearCache();
            _diskManager.ClearCache();
        }

        private void UpdateTiming()
        {
            DateTime now = DateTime.Now;
            double dt = (now - _lastTrafficTime).TotalSeconds;
            _lastTrafficTime = now;
            if (dt > 5.0) dt = 0;
            _secondAccumulator += dt;
            while (_secondAccumulator >= 1.0) { _secondAccumulator -= 1.0; _secondsCounter++; }
        }

        private static Dictionary<string, Dictionary<string, float>> ReadFromService(int timeoutMs = 800)
        {
            var result = new Dictionary<string, Dictionary<string, float>>();

            lock (_pipeLock)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", SensorService.PipeName, PipeDirection.In);
                    try { client.Connect(timeoutMs); }
                    catch (TimeoutException) { LogClient("Connect TIMEOUT"); return result; }

                    using var reader = new StreamReader(client, Encoding.UTF8);
                    string json = reader.ReadToEnd();

                    if (string.IsNullOrWhiteSpace(json)) return result;

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("sensors", out var sArr))
                    {
                        foreach (var hwElem in sArr.EnumerateArray())
                        {
                            string hwName = hwElem.GetProperty("name").GetString() ?? "";
                            var sensors = new Dictionary<string, float>();
                            foreach (var s in hwElem.GetProperty("sensors").EnumerateArray())
                            {
                                string sName = s.GetProperty("n").GetString() ?? "";
                                string sType = s.GetProperty("t").GetString() ?? "";
                                float v = s.GetProperty("v").GetSingle();
                                sensors[$"{sType}.{sName}"] = v;
                            }
                            result[hwName] = sensors;
                        }
                    }

                    if (root.TryGetProperty("hardware", out var hArr))
                    {
                        var list = new List<HardwareItem>();
                        foreach (var hwElem in hArr.EnumerateArray())
                        {
                            var item = new HardwareItem
                            {
                                Name = hwElem.GetProperty("name").GetString() ?? "",
                                Type = hwElem.GetProperty("type").GetString() ?? ""
                            };
                            foreach (var s in hwElem.GetProperty("sensors").EnumerateArray())
                            {
                                item.Sensors.Add(new SensorItem
                                {
                                    Name = s.GetProperty("n").GetString() ?? "",
                                    Type = s.GetProperty("t").GetString() ?? ""
                                });
                            }
                            list.Add(item);
                        }
                        _hardwareList = list;
                    }

                    if (root.TryGetProperty("cpuTemp", out var ct) && ct.ValueKind == JsonValueKind.Number)
                    {
                        _cpuTempFromService = ct.GetSingle();
                        if (_cpuTempFromService.Value > 0)
                            _lastValidMap["CPU.Temp"] = _cpuTempFromService.Value;
                    }

                    if (root.TryGetProperty("gpuTemp", out var gt) && gt.ValueKind == JsonValueKind.Number)
                    {
                        _gpuTempFromService = gt.GetSingle();
                        if (_gpuTempFromService.Value > 0)
                            _lastValidMap["GPU.Temp"] = _gpuTempFromService.Value;
                    }

                    if (root.TryGetProperty("cpuTempSource", out var src) && src.ValueKind == JsonValueKind.String)
                        _cpuTempSource = src.GetString() ?? "";

                    if (root.TryGetProperty("gpuTempSource", out var gsrc) && gsrc.ValueKind == JsonValueKind.String)
                        _gpuTempSource = gsrc.GetString() ?? "";

                    _readCount++;
                    if (_readCount <= 5 || _readCount % 30 == 0)
                    {
                        LogClient($"CPU.Temp={_cpuTempFromService:F1}({_cpuTempSource}) GPU.Temp={_gpuTempFromService:F1}({_gpuTempSource}) HW={_hardwareList.Count}");
                    }
                }
                catch (Exception ex)
                {
                    LogClient($"ERROR: {ex.Message}");
                }
            }
            return result;
        }

        private static void LogClient(string msg)
        {
            try { File.AppendAllText(ClientLogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        }

        // ================================================================
        // UI 下拉菜单：每次调用强制读服务
        // ================================================================
        private static readonly object _lock2 = new object();

        public static string GenerateSmartName(ISensor sensor, IHardware hardware) => "";

        private static DateTime _lastRefreshTime = DateTime.MinValue;

        private static void ForceRefreshHardwareList()
        {
         if ((DateTime.Now - _lastRefreshTime).TotalSeconds < 2.0) return;

         lock (_lock2)
         {
         if ((DateTime.Now - _lastRefreshTime).TotalSeconds < 2.0) return;
         try { ReadFromService(800); } catch { }
         _lastRefreshTime = DateTime.Now;
         }
        }

        public static List<string> ListAllNetworks()
        {
            lock (_lock2)
            {
                ForceRefreshHardwareList();
                return _hardwareList.Where(h => h.Type == "Network").Select(h => h.Name).ToList();
            }
        }

        public static List<string> ListAllDisks()
        {
            lock (_lock2)
            {
                ForceRefreshHardwareList();
                return _hardwareList.Where(h => h.Type == "Storage").Select(h => h.Name).ToList();
            }
        }

        public static List<string> ListAllGpus()
        {
            lock (_lock2)
            {
                ForceRefreshHardwareList();
                return _hardwareList.Where(h => h.Type.Contains("Gpu")).Select(h => h.Name).ToList();
            }
        }

        public static List<HardwareScanner.GpuOption> ListAllGpuOptions()
        {
            lock (_lock2)
            {
                ForceRefreshHardwareList();
                return _hardwareList
                    .Where(h => h.Type.Contains("Gpu"))
                    .Select(h => new HardwareScanner.GpuOption { Label = h.Name, Name = h.Name, Value = h.Name })
                    .ToList();
            }
        }

        public static List<string> ListAllFans()
        {
            lock (_lock2)
            {
                ForceRefreshHardwareList();
                return _hardwareList
                    .SelectMany(h => h.Sensors
                        .Where(s => s.Type == "Fan")
                        .Select(s => $"{s.Name} [{h.Name}]"))
                    .Distinct()
                    .ToList();
            }
        }

        public static List<string> ListAllMoboTemps()
        {
            lock (_lock2)
            {
                ForceRefreshHardwareList();
                return _hardwareList
                    .Where(h => h.Type == "Motherboard" || h.Type == "SuperIO")
                    .SelectMany(h => h.Sensors
                        .Where(s => s.Type == "Temperature")
                        .Select(s => $"{s.Name} [{h.Name}]"))
                    .Distinct()
                    .ToList();
            }
        }
    }
}