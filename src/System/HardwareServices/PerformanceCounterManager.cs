using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// Windows 性能计数器统一管理器
    /// <para>负责管理所有基于 System.Diagnostics.PerformanceCounter 的系统级监控指标。</para>
    /// <para>优势：比 LHM 更快、更准（尤其是 CPU 频率和内存占用），且不占用硬件总线。</para>
    /// </summary>
    public class PerformanceCounterManager : IDisposable
    {
        // --- 核心计数器实例 ---
        private PerformanceCounter? _cpuLoadCounter;
        private PerformanceCounter? _cpuFreqCounter;
        private PerformanceCounter? _ramAvailableCounter;
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;
        private PerformanceCounter? _diskActiveCounter;
        private PerformanceCounter? _uptimeCounter;

        // --- SMB 计数器 ---
        private PerformanceCounter? _smbClientReadCounter;
        private PerformanceCounter? _smbClientWriteCounter;
        private PerformanceCounter? _smbServerReadCounter;
        private PerformanceCounter? _smbServerWriteCounter;

        // --- GPU Engine 类别缓存 (任务管理器同源) ---
        private string? _gpuEngineCategoryName = null;

        // --- 静态基准数据 ---
        private float _cpuBaseFreq = 0;
        private float _totalMemoryMB = 0;

        public bool IsInitialized { get; private set; } = false;
        public float TotalMemoryGB => _totalMemoryMB / 1024f;

        /// <summary>
        /// 异步初始化所有计数器
        /// </summary>
        public void InitializeAsync()
        {
            Task.Run(() =>
            {
                try
                {
                    InitStaticHardwareInfo();

                    _cpuLoadCounter = CreateCounter("Processor Information", "% Processor Utility", "_Total");
                    if (_cpuLoadCounter == null)
                        _cpuLoadCounter = CreateCounter("Processor Information", "% Processor Time", "_Total");
                    if (_cpuLoadCounter == null)
                        _cpuLoadCounter = CreateCounter("Processor", "% Processor Time", "_Total");

                    _cpuFreqCounter = CreateCounter("Processor Information", "% Processor Performance", "_Total");
                    if (_cpuFreqCounter == null)
                        _cpuFreqCounter = CreateCounter("Processor", "% Processor Performance", "_Total");

                    _ramAvailableCounter = CreateCounter("Memory", "Available MBytes");
                    _diskReadCounter = CreateCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    _diskWriteCounter = CreateCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    _diskActiveCounter = CreateCounter("PhysicalDisk", "% Disk Time", "_Total");
                    _uptimeCounter = CreateCounter("System", "System Up Time");

                    _smbClientReadCounter = CreateCounter("SMB Client Shares", "Read Bytes/sec", "_Total");
                    _smbClientWriteCounter = CreateCounter("SMB Client Shares", "Write Bytes/sec", "_Total");
                    _smbServerReadCounter = CreateCounter("SMB Server Shares", "Received Bytes/sec", "_Total");
                    _smbServerWriteCounter = CreateCounter("SMB Server Shares", "Sent Bytes/sec", "_Total");

                    // ★ 探测 GPU Engine 类别（英文 / 中文）
                    InitGpuEngineCategory();

                    // 预热
                    SafeRead(_cpuLoadCounter);
                    SafeRead(_cpuFreqCounter);
                    SafeRead(_ramAvailableCounter);
                    SafeRead(_diskReadCounter);
                    SafeRead(_diskWriteCounter);
                    SafeRead(_diskActiveCounter);
                    SafeRead(_uptimeCounter);
                    SafeRead(_smbClientReadCounter);
                    SafeRead(_smbClientWriteCounter);
                    SafeRead(_smbServerReadCounter);
                    SafeRead(_smbServerWriteCounter);

                    // GPU 引擎预热（第一次 NextValue 返回 0）
                    try { GetGpuLoad(); } catch { }

                    IsInitialized = true;
                }
                catch { }
            });
        }

        private void InitGpuEngineCategory()
        {
            try
            {
                string[] candidates = { "GPU Engine", "GPU 引擎" };
                foreach (var name in candidates)
                {
                    try
                    {
                        if (PerformanceCounterCategory.Exists(name))
                        {
                            _gpuEngineCategoryName = name;
                            return;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private PerformanceCounter? CreateCounter(string category, string counter, string instance = "")
        {
            try
            {
                if (!PerformanceCounterCategory.Exists(category)) return null;
                return string.IsNullOrEmpty(instance)
                    ? new PerformanceCounter(category, counter)
                    : new PerformanceCounter(category, counter, instance);
            }
            catch { return null; }
        }

        private void InitStaticHardwareInfo()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (key != null)
                    {
                        var val = key.GetValue("~MHz");
                        if (val is int freq) _cpuBaseFreq = freq;
                    }
                }
            }
            catch { }
            if (_cpuBaseFreq <= 0) _cpuBaseFreq = 2500;

            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    _totalMemoryMB = memStatus.ullTotalPhys / 1024f / 1024f;
                }
            }
            catch { }
            if (_totalMemoryMB <= 0) _totalMemoryMB = 16 * 1024;
        }

        // ==========================================
        // 公共取值方法
        // ==========================================

        public float? GetCpuLoad()
        {
            var val = SafeRead(_cpuLoadCounter);
            return val > 100f ? 100f : val;
        }

        public float? GetCpuFreq()
        {
            var percent = SafeRead(_cpuFreqCounter);
            if (percent.HasValue && _cpuBaseFreq > 0)
                return _cpuBaseFreq * (percent.Value / 100.0f);
            return null;
        }

        public (float? Load, float? UsedGB) GetMemoryData()
        {
            var availableMB = SafeRead(_ramAvailableCounter);
            if (availableMB.HasValue && _totalMemoryMB > 0)
            {
                float usedMB = _totalMemoryMB - availableMB.Value;
                float load = (usedMB / _totalMemoryMB) * 100f;
                float usedGB = usedMB / 1024f;
                return (load, usedGB);
            }
            return (null, null);
        }

        public float? GetDiskRead() => SafeRead(_diskReadCounter);
        public float? GetDiskWrite() => SafeRead(_diskWriteCounter);
        public float? GetDiskActive() => SafeRead(_diskActiveCounter);
        public float? GetUptime() => SafeRead(_uptimeCounter);

        /// <summary>
        /// ★★★ 获取 GPU 总占用率（所有 3D 引擎累加，和任务管理器一致）★★★
        /// </summary>
        public float? GetGpuLoad()
        {
            if (string.IsNullOrEmpty(_gpuEngineCategoryName)) return null;

            try
            {
                var cat = new PerformanceCounterCategory(_gpuEngineCategoryName);
                float total = 0;
                bool found = false;

                foreach (var inst in cat.GetInstanceNames())
                {
                    if (!inst.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        using var pc = new PerformanceCounter(
                            _gpuEngineCategoryName, "Utilization Percentage", inst, readOnly: true);

                        // 第一次调用返回 0，需要连续两次
                        pc.NextValue();
                        System.Threading.Thread.Sleep(1);
                        float v = pc.NextValue();

                        if (v >= 0) total += v;
                        found = true;
                    }
                    catch { }
                }

                if (!found) return null;
                if (total > 100f) total = 100f;
                return total;
            }
            catch { return null; }
        }

        /// <summary>
        /// 获取本周期内估算的 SMB 流量增量
        /// </summary>
        public (long UpBytes, long DownBytes) GetEstimatedSmbBytes(double seconds)
        {
            var rate = GetSmbTrafficRate();
            const double OverheadFactor = 1.2;
            long up = (long)(rate.UpRate * seconds * OverheadFactor);
            long down = (long)(rate.DownRate * seconds * OverheadFactor);
            return (up, down);
        }

        private (float UpRate, float DownRate) GetSmbTrafficRate()
        {
            float up = 0;
            float down = 0;
            if (_smbClientWriteCounter != null) up += (SafeRead(_smbClientWriteCounter) ?? 0);
            if (_smbClientReadCounter != null) down += (SafeRead(_smbClientReadCounter) ?? 0);
            if (_smbServerWriteCounter != null) up += (SafeRead(_smbServerWriteCounter) ?? 0);
            if (_smbServerReadCounter != null) down += (SafeRead(_smbServerReadCounter) ?? 0);
            return (up, down);
        }

        private float? SafeRead(PerformanceCounter? pc)
        {
            if (pc == null) return null;
            try { return pc.NextValue(); }
            catch { return null; }
        }

        public void Dispose()
        {
            _cpuLoadCounter?.Dispose();
            _cpuFreqCounter?.Dispose();
            _ramAvailableCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            _diskActiveCounter?.Dispose();
            _uptimeCounter?.Dispose();
            _smbClientReadCounter?.Dispose();
            _smbClientWriteCounter?.Dispose();
            _smbServerReadCounter?.Dispose();
            _smbServerWriteCounter?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}