using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    public class HardwareValueProvider : IDisposable
    {
        private readonly Computer _computer;
        private readonly Settings _cfg;
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly FpsCounter _fpsCounter;
        private readonly object _lock;
        private readonly Dictionary<string, float> _lastValidMap;

        private readonly PerformanceCounterManager _perfManager;
        private readonly ComponentProcessor _componentProcessor;

        private readonly Dictionary<string, float> _tickCache = new();
        private Dictionary<string, ISensor> _manualSensorCache = new();

        private const float AutoMoboTempHardMax = 95f;
        private const float ManualMoboTempHardMax = 125f;

        private string _lastPrefCpuFan = "";
        private string _lastPrefCpuPump = "";
        private string _lastPrefCaseFan = "";
        private string _lastPrefMoboTemp = "";
        private string _lastPrefDisk = "";
        private string _lastPrefNet = "";
        private string _lastPrefGpu = "";

        public HardwareValueProvider(Computer c, Settings s, SensorMap map, NetworkManager net, DiskManager disk, FpsCounter fpsCounter, PerformanceCounterManager perfManager, object syncLock, Dictionary<string, float> lastValid)
        {
            _computer = c;
            _cfg = s;
            _sensorMap = map;
            _networkManager = net;
            _diskManager = disk;
            _fpsCounter = fpsCounter;
            _perfManager = perfManager;
            _lock = syncLock;
            _lastValidMap = lastValid;

            _componentProcessor = new ComponentProcessor(c, s, map);
        }

        public void PreCacheAllSensors(SensorMap map)
        {
            var newCache = map.GetInternalMap();

            _lastPrefCpuFan = _cfg.PreferredCpuFan;
            _lastPrefCpuPump = _cfg.PreferredCpuPump;
            _lastPrefCaseFan = _cfg.PreferredCaseFan;
            _lastPrefMoboTemp = _cfg.PreferredMoboTemp;
            _lastPrefDisk = _cfg.PreferredDisk;
            _lastPrefNet = _cfg.PreferredNetwork;
            _lastPrefGpu = _cfg.PreferredGpu ?? "";

            string[] preferredKeys = { "CPU.Fan", "CPU.Pump", "CASE.Fan", "MOBO.Temp" };
            foreach (var key in preferredKeys)
            {
                string pref = (key == "CPU.Fan") ? _lastPrefCpuFan :
                             (key == "CPU.Pump") ? _lastPrefCpuPump :
                             (key == "CASE.Fan") ? _lastPrefCaseFan : _lastPrefMoboTemp;

                SensorType type = (key == "MOBO.Temp") ? SensorType.Temperature : SensorType.Fan;

                if (!string.IsNullOrEmpty(pref) && !pref.Contains("自动") && !pref.Contains("Auto"))
                {
                    var s = FindSensorReverse(pref, type);
                    if (s != null) newCache[key] = s;
                }
            }

            lock (_lock)
            {
                _manualSensorCache = newCache;
                _tickCache.Clear();
                _componentProcessor.ClearCache();
            }
        }

        private ISensor? FindSensorReverse(string savedString, SensorType type)
        {
            if (string.IsNullOrEmpty(savedString) || savedString.Contains("Auto") || savedString.Contains("自动"))
                return null;

            int idx = savedString.LastIndexOf('[');
            if (idx < 0) return null;

            string targetSensorName = savedString.Substring(0, idx).Trim();
            string targetHardwareName = savedString.Substring(idx + 1).TrimEnd(']');

            ISensor? SearchBranch(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType == type && s.Name == targetSensorName)
                        return s;
                }
                foreach (var sub in h.SubHardware)
                {
                    var s = SearchBranch(sub);
                    if (s != null) return s;
                }
                return null;
            }

            foreach (var hw in _computer.Hardware)
            {
                if (hw.Name == targetHardwareName)
                    return SearchBranch(hw);
            }
            return null;
        }

        public void ClearCache()
        {
            lock (_lock)
            {
                _manualSensorCache.Clear();
                _tickCache.Clear();
                _componentProcessor.ClearCache();
            }
        }

        public void OnUpdateTickStarted()
        {
            lock (_lock)
            {
                _tickCache.Clear();

                bool gpuChanged = _lastPrefGpu != (_cfg.PreferredGpu ?? "");
                if (gpuChanged)
                    _sensorMap.Rebuild(_computer, _cfg);

                if (gpuChanged ||
                    _lastPrefCpuFan != _cfg.PreferredCpuFan ||
                    _lastPrefCpuPump != _cfg.PreferredCpuPump ||
                    _lastPrefCaseFan != _cfg.PreferredCaseFan ||
                    _lastPrefMoboTemp != _cfg.PreferredMoboTemp ||
                    _lastPrefDisk != _cfg.PreferredDisk ||
                    _lastPrefNet != _cfg.PreferredNetwork)
                {
                    PreCacheAllSensors(_sensorMap);
                }
            }
        }

        public float? GetStartupValue(string key)
        {
            if (_lastValidMap.TryGetValue(key, out float lastVal)) return lastVal;

            bool useCounter = _cfg.UseWinPerCounters && _perfManager.IsInitialized;
            if (!useCounter) return null;

            if (key == "CPU.Load")
            {
                var cpuLoad = _perfManager.GetCpuLoad();
                return cpuLoad.HasValue ? Math.Clamp(cpuLoad.Value, 0f, 100f) : null;
            }
            if (key == "CPU.Clock") return _perfManager.GetCpuFreq();
            if (key == "GPU.Load")
            {
                var gpuLoad = _perfManager.GetGpuLoad();
                return gpuLoad.HasValue ? Math.Clamp(gpuLoad.Value, 0f, 100f) : null;
            }
            if (key == "MEM.Load")
            {
                var memData = _perfManager.GetMemoryData();
                if (memData.Load.HasValue && Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                return memData.Load;
            }
            if (key == "DISK.Read") return _perfManager.GetDiskRead();
            if (key == "DISK.Write") return _perfManager.GetDiskWrite();
            if (key == "DISK.Activity")
            {
                var diskActive = _perfManager.GetDiskActive();
                return diskActive.HasValue ? Math.Clamp(diskActive.Value, 0f, 100f) : null;
            }
            return null;
        }

        public float? GetValue(string key)
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_lock, 10, ref lockTaken);
                if (!lockTaken)
                {
                    if (_lastValidMap.TryGetValue(key, out float lastVal)) return lastVal;
                    return null;
                }

                if (_tickCache.TryGetValue(key, out float cachedVal)) return cachedVal;

                // ============================================================
                // ★★★ 任务管理器同源：CPU / GPU / 内存 优先走 PerformanceCounter ★★★
                // ============================================================
                bool useCounter = _cfg.UseWinPerCounters && _perfManager.IsInitialized;

                if (useCounter)
                {
                    switch (key)
                    {
                        case "CPU.Load":
                            var cpuLoad = _perfManager.GetCpuLoad();
                            if (cpuLoad.HasValue)
                            {
                                float v = Math.Clamp(cpuLoad.Value, 0f, 100f);
                                _lastValidMap[key] = v;
                                _tickCache[key] = v;
                                return v;
                            }
                            break;

                        case "GPU.Load":
                            var gpuLoad = _perfManager.GetGpuLoad();
                            if (gpuLoad.HasValue && gpuLoad.Value > 0)
                            {
                                float v = Math.Clamp(gpuLoad.Value, 0f, 100f);
                                _lastValidMap[key] = v;
                                _tickCache[key] = v;
                                return v;
                            }
                            // 拿不到就走服务快照兜底
                            if (_lastValidMap.TryGetValue(key, out float svcGpu) && svcGpu >= 0f)
                            {
                                _tickCache[key] = svcGpu;
                                return svcGpu;
                            }
                            break;

                        case "MEM.Load":
                            var memData = _perfManager.GetMemoryData();
                            if (memData.Load.HasValue)
                            {
                                float v = Math.Clamp(memData.Load.Value, 0f, 100f);
                                if (Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                                _lastValidMap[key] = v;
                                _tickCache[key] = v;
                                return v;
                            }
                            break;
                    }
                }

                // ============================================================
                // 服务快照优先白名单（温度等主程序读不到的项）
                // ============================================================
                switch (key)
                {
                    case "CPU.Temp":
                    case "GPU.Temp":
                    case "DISK.Temp":
                        if (_lastValidMap.TryGetValue(key, out float svcTemp) && svcTemp > 0f)
                        {
                            _tickCache[key] = svcTemp;
                            return svcTemp;
                        }
                        break;

                    case "CPU.Power":
                    case "CPU.Clock":
                    case "GPU.Power":
                    case "GPU.Clock":
                    case "GPU.Fan":
                    case "GPU.VRAM.Used":
                    case "GPU.VRAM.Total":
                    case "DISK.Read":
                    case "DISK.Write":
                    case "DISK.Activity":
                        if (_lastValidMap.TryGetValue(key, out float svcVal) && svcVal >= 0f)
                        {
                            _tickCache[key] = svcVal;
                            return svcVal;
                        }
                        break;
                }
                // ============================================================

                float? result = null;
                bool skipGenericFallback = false;

                switch (key)
                {
                    case "CPU.Load":
                        if (_manualSensorCache.TryGetValue("CPU.Load", out var totalSensor) && totalSensor.Value.HasValue)
                            result = totalSensor.Value.Value;
                        if (result == null) result = _componentProcessor.GetCpuLoad();
                        if (result == null) result = 0f;
                        break;

                    case "CPU.Temp":
                        result = _componentProcessor.GetCpuTemp();
                        if (result == null && _manualSensorCache.TryGetValue("CPU.Temp", out var fallbackT))
                            result = fallbackT.Value;
                        if (result == null) result = 0f;
                        break;

                    case "DATA.DayUp":
                        result = TrafficLogger.GetTodayStats().up;
                        break;
                    case "DATA.DayDown":
                        result = TrafficLogger.GetTodayStats().down;
                        break;

                    case "MEM.Load":
                        // PerformanceCounter 已优先处理，这里只做兜底
                        {
                            float? usedVal = null;
                            float? availVal = null;
                            if (_manualSensorCache.TryGetValue("MEM.Used", out var u) && u.Value.HasValue) usedVal = u.Value.Value;
                            if (_manualSensorCache.TryGetValue("MEM.Available", out var a) && a.Value.HasValue) availVal = a.Value.Value;
                            if (usedVal.HasValue && availVal.HasValue)
                            {
                                float memTotal = usedVal.Value + availVal.Value;
                                if (memTotal > 0)
                                {
                                    if (Settings.DetectedRamTotalGB <= 0)
                                        Settings.DetectedRamTotalGB = memTotal > 512.0f ? memTotal / 1024.0f : memTotal;
                                    result = (usedVal.Value / memTotal) * 100.0f;
                                }
                            }
                            if (result == null && _manualSensorCache.TryGetValue("MEM.Load", out var sLoad) && sLoad.Value.HasValue)
                                result = sLoad.Value.Value;
                        }
                        break;

                    case "GPU.VRAM":
                        float? used = GetValue("GPU.VRAM.Used");
                        float? total = GetValue("GPU.VRAM.Total");
                        if (used.HasValue && total.HasValue && total > 0)
                        {
                            if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                            if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                            result = used / total * 100f;
                        }
                        else
                        {
                            if (_manualSensorCache.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue) result = s.Value;
                        }
                        break;

                    case "CPU.Fan":
                    case "CPU.Pump":
                    case "CASE.Fan":
                    case "GPU.Fan":
                        if (_manualSensorCache.TryGetValue(key, out var sFan))
                            result = sFan.Value;
                        else
                        {
                            if (_manualSensorCache.TryGetValue(key, out var autoS) && autoS.Value.HasValue)
                                result = autoS.Value.Value;
                        }
                        if (result.HasValue && result.Value < 10000f)
                            _cfg.UpdateMaxRecord(key, result.Value);
                        break;

                    case "MOBO.Temp":
                        skipGenericFallback = true;
                        if (_manualSensorCache.TryGetValue(key, out var sMobo))
                            result = ReadMoboTemperature(sMobo);
                        break;

                    case "FPS":
                        result = _fpsCounter.GetFps();
                        break;

                    case "BAT.Percent":
                    case "BAT.Power":
                    case "BAT.Voltage":
                    case "BAT.Current":
                        result = BatteryService.GetBatteryValue(key, _manualSensorCache);
                        break;

                    default:
                        if (key.StartsWith("NET"))
                        {
                            if (_manualSensorCache.TryGetValue(key, out var sNet))
                                result = sNet.Value;
                            if (result == null)
                                result = _networkManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                        }
                        else if (key.StartsWith("DISK"))
                        {
                            bool isSpecificDisk = !string.IsNullOrEmpty(_cfg.PreferredDisk);
                            if (useCounter && !isSpecificDisk)
                            {
                                if (key == "DISK.Read") result = _perfManager.GetDiskRead();
                                else if (key == "DISK.Write") result = _perfManager.GetDiskWrite();
                                else if (key == "DISK.Activity")
                                {
                                    result = _perfManager.GetDiskActive();
                                    if (result.HasValue) result = Math.Clamp(result.Value, 0f, 100f);
                                }
                            }
                            if (result == null && _manualSensorCache.TryGetValue(key, out var sDisk))
                                result = sDisk.Value;
                            if (result == null)
                                result = _diskManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                        }
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                                result = _perfManager.GetCpuFreq();
                            if (result == null)
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                        }
                        break;
                }

                if (!skipGenericFallback && result == null && _manualSensorCache.TryGetValue(key, out var sGen))
                {
                    var val = sGen.Value;
                    if (val.HasValue && !float.IsNaN(val.Value))
                    {
                        _lastValidMap[key] = val.Value;
                        result = val.Value;
                    }
                    else if (_lastValidMap.TryGetValue(key, out var last))
                        result = last;
                }

                if (result == null && _lastValidMap.TryGetValue(key, out float svcLast) && svcLast > 0f)
                    result = svcLast;

                if (result.HasValue)
                {
                    _tickCache[key] = result.Value;
                    return result.Value;
                }

                return null;
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_lock);
            }
        }

        private float? ReadMoboTemperature(ISensor sensor)
        {
            bool manualSensor = IsManualMoboTemperatureSelected();
            var raw = sensor.Value;
            if (!raw.HasValue || float.IsNaN(raw.Value) || float.IsInfinity(raw.Value) || raw.Value <= 0f)
                return GetLastValidMoboTemperature(manualSensor);

            float value = raw.Value;
            bool overHardMax = manualSensor ? value > ManualMoboTempHardMax : value >= AutoMoboTempHardMax;
            if (overHardMax)
                return GetLastValidMoboTemperature(manualSensor);

            _lastValidMap["MOBO.Temp"] = value;
            return value;
        }

        private bool IsManualMoboTemperatureSelected()
        {
            string pref = _cfg.PreferredMoboTemp ?? "";
            return !string.IsNullOrWhiteSpace(pref) &&
                   !pref.Contains("自动", StringComparison.OrdinalIgnoreCase) &&
                   !pref.Contains("Auto", StringComparison.OrdinalIgnoreCase);
        }

        private float? GetLastValidMoboTemperature(bool manualSensor)
        {
            float max = manualSensor ? ManualMoboTempHardMax : AutoMoboTempHardMax;
            return _lastValidMap.TryGetValue("MOBO.Temp", out float last) && last > 0f && last <= max ? last : null;
        }

        public void UpdateFromSnapshot(Dictionary<string, Dictionary<string, float>> snapshot)
        {
            lock (_lock)
            {
                _tickCache.Clear();
                foreach (var hw in snapshot)
                {
                    foreach (var sensor in hw.Value)
                    {
                        string key = sensor.Key;
                        float value = sensor.Value;

                        if (value < 0f || float.IsNaN(value)) continue;

                        _lastValidMap[key] = value;

                        int dotIdx = key.IndexOf('.');
                        if (dotIdx < 0) continue;

                        string sType = key.Substring(0, dotIdx);
                        string sName = key.Substring(dotIdx + 1);

                        if (sType == "Load")
                        {
                            // ★ 注意：CPU.Load 和 GPU.Load 优先走 PerformanceCounter 和任务管理器一致
                            // 服务数据只作为兜底
                            if (sName == "CPU Total" && !_lastValidMap.ContainsKey("CPU.Load"))
                                _lastValidMap["CPU.Load"] = value;

                            if ((sName == "D3D 3D" || sName == "GPU Core") && !_lastValidMap.ContainsKey("GPU.Load"))
                                _lastValidMap["GPU.Load"] = value;

                            if (sName == "Memory" && hw.Key.Contains("Total Memory") && !_lastValidMap.ContainsKey("MEM.Load"))
                                _lastValidMap["MEM.Load"] = value;

                            if (sName == "Used Space")
                                _lastValidMap["DISK.Used"] = value;
                        }
                        else if (sType == "Temperature")
                        {
                            if (sName.Contains("CPU") || sName.Contains("Package"))
                                _lastValidMap["CPU.Temp"] = value;
                            if (sName.Contains("GPU") || sName.Contains("Graphics"))
                                _lastValidMap["GPU.Temp"] = value;
                            if (sName == "Temperature")
                                _lastValidMap["DISK.Temp"] = value;
                        }
                        else if (sType == "Clock")
                        {
                            if (sName.Contains("GPU")) _lastValidMap["GPU.Clock"] = value;
                            else if (sName.Contains("CPU") || sName.Contains("Core")) _lastValidMap["CPU.Clock"] = value;
                        }
                        else if (sType == "Power")
                        {
                            if (sName.Contains("CPU") || sName.Contains("Package"))
                                _lastValidMap["CPU.Power"] = value;
                            else if (sName.Contains("GPU") || sName.Contains("Graphics"))
                                _lastValidMap["GPU.Power"] = value;
                        }
                        else if (sType == "SmallData")
                        {
                            if (sName == "D3D Shared Memory Used")
                                _lastValidMap["GPU.VRAM.Used"] = value;
                            if (sName == "D3D Shared Memory Total")
                                _lastValidMap["GPU.VRAM.Total"] = value;
                        }
                        else if (sType == "Throughput")
                        {
                            if (sName == "Read Rate") _lastValidMap["DISK.Read"] = value;
                            if (sName == "Write Rate") _lastValidMap["DISK.Write"] = value;
                        }
                        else if (sType == "Data")
                        {
                            if (sName == "Memory Used" && hw.Key.Contains("Total Memory"))
                                _lastValidMap["MEM.Used"] = value;
                            if (sName == "Memory Available" && hw.Key.Contains("Total Memory"))
                                _lastValidMap["MEM.Available"] = value;
                        }
                    }
                }
            }
        }

        public void Dispose() { }
    }
}