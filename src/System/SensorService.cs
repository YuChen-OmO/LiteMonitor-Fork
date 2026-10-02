using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    public class SensorService : ServiceBase
    {
        public const string PipeName = "LiteMonitorSensorPipe";
        private const string LogPath = @"C:\LiteMonitor_Service.log";

        private readonly Computer _computer;
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;

        private static bool _msrAvailable = false;
        private static int _tjMaxCpu = 100;
        private static int _tjMaxGpu = 100;
        private static Type? _ring0Type = null;

        private static readonly List<float> _recentWmiTemps = new();

        private float? _lastValidCpuTemp = null;
        private float? _lastValidGpuTemp = null;

        private static DateTime _lastThermalLogCpu = DateTime.MinValue;
        private static DateTime _lastThermalLogGpu = DateTime.MinValue;

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        }

        private static void LogThermalCpu(string msg)
        {
            if ((DateTime.Now - _lastThermalLogCpu).TotalSeconds < 30) return;
            _lastThermalLogCpu = DateTime.Now;
            Log(msg);
        }

        private static void LogThermalGpu(string msg)
        {
            if ((DateTime.Now - _lastThermalLogGpu).TotalSeconds < 30) return;
            _lastThermalLogGpu = DateTime.Now;
            Log(msg);
        }

        public SensorService()
        {
            ServiceName = "LiteMonitorSensorService";
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsBatteryEnabled = false,
                IsPsuEnabled = false
            };
        }

        protected override void OnStart(string[] args)
        {
            Log("=== OnStart ===");
            _cts = new CancellationTokenSource();

            try
            {
                _computer.Open();
                Log($"Computer.Open OK, hardware count = {_computer.Hardware.Count}");
                foreach (var hw in _computer.Hardware)
                    Log($"  - {hw.HardwareType}: {hw.Name}");
            }
            catch (Exception ex)
            {
                Log($"Computer.Open FAILED: {ex}");
                throw;
            }

            // 初始化热模型环境参数（可根据用户设置调整）
            ThermalModel.SetRoomTemp(25f);
            ThermalModel.SetPressure(101325f);
            ThermalModel.SetAgeHours(1000f);

            TryInitMsrAndDts();
            Task.Run(() => WorkerLoop(_cts.Token));
        }

        protected override void OnStop()
        {
            Log("=== OnStop ===");
            _cts?.Cancel();
            try { _computer.Close(); } catch { }
        }

        // ============================================================
        // MSR 初始化
        // ============================================================
        private void TryInitMsrAndDts()
        {
            try
            {
                var asm = typeof(Computer).Assembly;
                string[] candidates = {
                    "LibreHardwareMonitor.Hardware.Ring0",
                    "LibreHardwareMonitor.Ring0",
                    "LibreHardwareMonitor.Hardware.OpCode",
                    "LibreHardwareMonitor.Hardware.KernelDriver"
                };

                foreach (var name in candidates)
                {
                    _ring0Type = asm.GetType(name);
                    if (_ring0Type != null) { Log($"MSR: Found at {name}"); break; }
                }

                if (_ring0Type == null) { Log("MSR: Ring0 NOT FOUND"); return; }

                var openMethod = _ring0Type.GetMethod("Open",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                openMethod?.Invoke(null, null);

                var isOpenProp = _ring0Type.GetProperty("IsOpen",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                bool isOpen = (bool)(isOpenProp?.GetValue(null) ?? false);
                Log($"MSR: Ring0.IsOpen = {isOpen}");

                if (!isOpen) return;

                _msrAvailable = true;

                int cpuTj = ReadMsr((uint)0x1A2, 16, 0xFF);
                if (cpuTj > 50 && cpuTj < 130) _tjMaxCpu = cpuTj;

                int gpuTj = ReadMsr((uint)0x1A2, 8, 0xFF);
                if (gpuTj > 50 && gpuTj < 130) _tjMaxGpu = gpuTj;
                else _tjMaxGpu = _tjMaxCpu;

                Log($"MSR: CPU TjMax = {_tjMaxCpu}, GPU TjMax = {_tjMaxGpu}");
            }
            catch (Exception ex)
            {
                Log($"MSR Init FAILED: {ex}");
            }
        }

        private int ReadMsr(uint msr, int shift, uint mask)
        {
            try
            {
                if (_ring0Type == null) return -1;
                var rdmsr = _ring0Type.GetMethod("Rdmsr",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (rdmsr == null) return -1;

                object[] args = new object[] { msr, 0u, 0u };
                bool ok = (bool)(rdmsr.Invoke(null, args) ?? false);
                if (!ok) return -1;

                uint eax = (uint)args[1];
                return (int)((eax >> shift) & mask);
            }
            catch { return -1; }
        }

        // ============================================================
        // 硬件输入采集
        // ============================================================
        private ThermalModel.Inputs CollectCpuInputs(float cpuLoad)
        {
            var inp = new ThermalModel.Inputs { Load = cpuLoad };

            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.Cpu) continue;

                    float maxCoreFreq = 0f;
                    float sumCoreFreq = 0f;
                    int coreCount = 0;

                    foreach (var s in hw.Sensors)
                    {
                        if (!s.Value.HasValue) continue;
                        string n = s.Name.ToLowerInvariant();

                        if (s.SensorType == SensorType.Clock && n.Contains("core") && !n.Contains("bus"))
                        {
                            float f = s.Value.Value / 1000f;
                            sumCoreFreq += f;
                            coreCount++;
                            if (f > maxCoreFreq) maxCoreFreq = f;
                        }

                        if (s.SensorType == SensorType.Voltage && inp.Voltage <= 0.1f)
                        {
                            if (n.Contains("core") || n.Contains("vcore") || n.Contains("vid"))
                            {
                                float v = s.Value.Value;
                                if (v > 0.3f && v < 2.0f) inp.Voltage = v;
                            }
                        }

                        if (s.SensorType == SensorType.Current &&
                            (n.Contains("core") || n.Contains("cpu")))
                            inp.Current = s.Value.Value;

                        if (s.SensorType == SensorType.Temperature)
                        {
                            if (n.Contains("hs2") || n.Contains("heatsink2")) inp.Hs2 = s.Value.Value;
                            else if (n.Contains("hs1") || n.Contains("heatsink1")) inp.Hs1 = s.Value.Value;
                            else if (n.Contains("heatsink") || n.Contains("散热")) inp.Hs1 ??= s.Value.Value;
                        }
                    }

                    inp.FreqGHz = coreCount > 0 ? sumCoreFreq / coreCount : maxCoreFreq;
                }
            }
            catch { }

            return inp;
        }

        private ThermalModel.Inputs CollectGpuInputs(float gpuLoad, out bool isIntegrated)
        {
            var inp = new ThermalModel.Inputs { Load = gpuLoad };
            isIntegrated = false;

            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.GpuIntel) isIntegrated = true;

                    if (hw.HardwareType != HardwareType.GpuIntel &&
                        hw.HardwareType != HardwareType.GpuNvidia &&
                        hw.HardwareType != HardwareType.GpuAmd) continue;

                    foreach (var s in hw.Sensors)
                    {
                        if (!s.Value.HasValue) continue;
                        string n = s.Name.ToLowerInvariant();

                        if (s.SensorType == SensorType.Clock && n.Contains("core"))
                        {
                            float f = s.Value.Value / 1000f;
                            if (f > inp.FreqGHz) inp.FreqGHz = f;
                        }

                        if (s.SensorType == SensorType.Voltage && inp.Voltage <= 0.1f)
                        {
                            float v = s.Value.Value;
                            if (v > 0.3f && v < 2.0f) inp.Voltage = v;
                        }

                        if (s.SensorType == SensorType.Current) inp.Current = s.Value.Value;

                        if (s.SensorType == SensorType.Temperature)
                        {
                            if (n.Contains("hs2") || n.Contains("heatsink2")) inp.Hs2 = s.Value.Value;
                            else if (n.Contains("hs1") || n.Contains("heatsink1")) inp.Hs1 = s.Value.Value;
                        }
                    }
                }
            }
            catch { }

            return inp;
        }

        // ============================================================
        // CPU 温度：MSR → LHM → WMI(波动检测) → 热模型
        // ============================================================
        private (float value, string source) ReadCpuTemp(float cpuLoad)
        {
            // 1) MSR
            float? msr = ReadCpuTempFromMsr();
            if (msr.HasValue && IsValidTemp(msr.Value))
            {
                _lastValidCpuTemp = msr;
                return (msr.Value, "MSR");
            }

            // 2) LHM
            float? lhm = ReadCpuTempFromLhm();
            if (lhm.HasValue && IsValidTemp(lhm.Value))
            {
                _lastValidCpuTemp = lhm;
                return (lhm.Value, "LHM");
            }

            // 3) WMI（波动检测）
            float? wmi = TryReadWmiCpuTemp();
            if (wmi.HasValue && IsValidTemp(wmi.Value))
            {
                _recentWmiTemps.Add(wmi.Value);
                if (_recentWmiTemps.Count > 30) _recentWmiTemps.RemoveAt(0);

                if (_recentWmiTemps.Count < 10)
                {
                    _lastValidCpuTemp = wmi;
                    return (wmi.Value, "WMI");
                }

                float range = _recentWmiTemps.Max() - _recentWmiTemps.Min();
                if (range > 1.0f)
                {
                    _lastValidCpuTemp = wmi;
                    return (wmi.Value, "WMI");
                }
            }

            // 4) 热模型（六套方案自动选择，高精度升级版）
            var inputs = CollectCpuInputs(cpuLoad);
            float tHot = ThermalModel.EstimateCpu(inputs);
            string method = ThermalModel.SelectMethod(inputs).ToString();

            LogThermalCpu($"ThermalModel CPU: load={cpuLoad:F0}% V={inputs.Voltage:F2}V f={inputs.FreqGHz:F2}GHz method={method} -> {tHot:F1}℃");

            _lastValidCpuTemp = tHot;
            return (tHot, $"Thermal:{method}");
        }

        private float? ReadCpuTempFromMsr()
        {
            if (!_msrAvailable) return null;
            try
            {
                int offset = ReadMsr((uint)0x19C, 16, 0x7F);
                if (offset <= 0 || offset == 0x7F) return null;
                float temp = _tjMaxCpu - offset;
                if (IsValidTemp(temp)) return temp;
            }
            catch { }
            return null;
        }

        private float? ReadCpuTempFromLhm()
        {
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.Cpu) continue;
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (!s.Value.HasValue) continue;
                        string n = s.Name;
                        if (n.Contains("Package") || n.Contains("CPU Core") ||
                            n.Contains("Core #1") || n.Contains("CPU"))
                        {
                            float v = s.Value.Value;
                            if (IsValidTemp(v)) return v;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // ============================================================
        // GPU 温度：DTS-MSR → DTS-LHM → 热模型（核显特判）
        // ============================================================
        private (float value, string source) ReadGpuTemp(float cpuTemp, float cpuLoad, float gpuLoad)
        {
            // 1) DTS-MSR
            float? dtsMsr = ReadGpuTempFromDts();
            if (dtsMsr.HasValue && IsValidTemp(dtsMsr.Value))
            {
                _lastValidGpuTemp = dtsMsr;
                return (dtsMsr.Value, "DTS-MSR");
            }

            // 2) DTS-LHM
            float? lhmGpu = ReadGpuTempFromLhm();
            if (lhmGpu.HasValue && IsValidTemp(lhmGpu.Value))
            {
                _lastValidGpuTemp = lhmGpu;
                return (lhmGpu.Value, "DTS-LHM");
            }

            // 3) 热模型（核显走 CPU 共享，独显走完整六套方案）
            var inputs = CollectGpuInputs(gpuLoad, out bool isIntegrated);
            float tGpu = ThermalModel.EstimateGpu(inputs, isIntegrated, cpuTemp);
            string method = isIntegrated ? "CpuShared" : ThermalModel.SelectMethod(inputs).ToString();

            LogThermalGpu($"ThermalModel GPU: load={gpuLoad:F0}% V={inputs.Voltage:F2}V f={inputs.FreqGHz:F2}GHz integrated={isIntegrated} method={method} -> {tGpu:F1}℃");

            _lastValidGpuTemp = tGpu;
            return (tGpu, $"Thermal:{method}");
        }

        private float? ReadGpuTempFromLhm()
        {
            try
            {
                foreach (var hw in _computer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.GpuIntel &&
                        hw.HardwareType != HardwareType.GpuNvidia &&
                        hw.HardwareType != HardwareType.GpuAmd) continue;

                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Temperature) continue;
                        if (!s.Value.HasValue) continue;
                        float v = s.Value.Value;
                        if (IsValidTemp(v)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        private float? ReadGpuTempFromDts()
        {
            if (!_msrAvailable) return null;
            try
            {
                int offset = ReadMsr((uint)0x1B1, 16, 0x7F);
                if (offset > 0 && offset != 0x7F)
                {
                    float temp = _tjMaxGpu - offset;
                    if (IsValidTemp(temp)) return temp;
                }

                int gpuOffset = ReadMsr((uint)0x1A2, 0, 0xFF);
                if (gpuOffset > 0 && gpuOffset < 100 && gpuOffset != _tjMaxGpu)
                {
                    float temp = _tjMaxCpu - gpuOffset;
                    if (IsValidTemp(temp)) return temp;
                }
            }
            catch { }
            return null;
        }

        private static bool IsValidTemp(float t)
        {
            return t > 0f && t < 120f && !float.IsNaN(t);
        }

        private void WorkerLoop(CancellationToken token)
        {
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        lock (_lock)
                        {
                            foreach (var hw in _computer.Hardware)
                            {
                                hw.Update();
                                foreach (var sub in hw.SubHardware) sub.Update();
                            }
                        }
                    }
                    catch { }
                    try { await Task.Delay(1000, token); } catch { break; }
                }
            }, token);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.Out,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    server.WaitForConnectionAsync(token).Wait(token);

                    string json;
                    lock (_lock) { json = BuildJson(); }

                    var bytes = Encoding.UTF8.GetBytes(json);
                    server.Write(bytes, 0, bytes.Length);
                    server.Flush();
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private string BuildJson()
        {
            var sensors = new List<object>();
            var hardware = new List<object>();

            float cpuLoad = 0;
            float gpuLoad = 0;

            foreach (var hw in _computer.Hardware)
            {
                AppendHardware(hw, sensors, hardware, ref cpuLoad, ref gpuLoad);
                foreach (var sub in hw.SubHardware)
                    AppendHardware(sub, sensors, hardware, ref cpuLoad, ref gpuLoad);
            }

            var cpuResult = ReadCpuTemp(cpuLoad);
            float cpuTemp = cpuResult.value;
            string cpuSource = cpuResult.source;

            var gpuResult = ReadGpuTemp(cpuTemp, cpuLoad, gpuLoad);
            float gpuTemp = gpuResult.value;
            string gpuSource = gpuResult.source;

            var root = new
            {
                sensors,
                hardware,
                cpuTemp,
                cpuTempSource = cpuSource,
                gpuTemp,
                gpuTempSource = gpuSource,
                cpuLoad,
                gpuLoad
            };

            return JsonSerializer.Serialize(root);
        }

        private void AppendHardware(IHardware hw, List<object> sensors, List<object> hardware,
            ref float cpuLoad, ref float gpuLoad)
        {
            var sList = new List<object>();
            var sListSimple = new List<object>();

            foreach (var s in hw.Sensors)
            {
                if (!s.Value.HasValue) continue;
                sList.Add(new { n = s.Name, t = s.SensorType.ToString(), v = s.Value.Value });
                sListSimple.Add(new { n = s.Name, t = s.SensorType.ToString() });

                if (s.SensorType == SensorType.Load)
                {
                    if (s.Name == "CPU Total") cpuLoad = s.Value.Value;
                    if (s.Name == "D3D 3D" || s.Name == "GPU Core") gpuLoad = s.Value.Value;
                }
            }

            sensors.Add(new { name = hw.Name, type = hw.HardwareType.ToString(), sensors = sList });
            hardware.Add(new { name = hw.Name, type = hw.HardwareType.ToString(), sensors = sListSimple });
        }

        private static float? TryReadWmiCpuTemp()
        {
            try
            {
                var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject obj in searcher.Get())
                {
                    object cur = obj["CurrentTemperature"];
                    if (cur == null) continue;
                    double kelvin10 = Convert.ToDouble(cur);
                    double celsius = (kelvin10 - 2732) / 10.0;
                    if (celsius > 0 && celsius < 120) return (float)celsius;
                }
            }
            catch { }
            return null;
        }
    }
}