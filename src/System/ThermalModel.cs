using System;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// CPU/GPU 热点温度热模型（高精度升级版）
    ///
    /// 优先级：
    ///   双点鳍片 > 单点鳍片 > 低端A(有T_hs) > 低端B(无T_hs) > 线性回退
    ///
    /// 全局修正（自动内嵌）：
    ///   气压换热、空气粘度、老化漂移、二阶热点、鳍片湍流、辐射散热
    ///
    /// 注意：本模型只在 MSR/LHM/WMI 全部失败后作为最终 fallback 使用。
    /// </summary>
    public static class ThermalModel
    {
        // ==================== 物理常数（高精度） ====================
        private const double Eg = 1.12;
        private const double KB = 8.617333262e-5;
        private const double Tref = 30.0;
        private const double K0 = 273.15;
        private const double ConvEps = 0.03;
        private const int MaxIter = 30;

        // ==================== 环境（可由外部设置） ====================
        public static float TRoom { get; set; } = 25f;
        public static float PAtm { get; set; } = 101325f;
        public static float TAgeHours { get; set; } = 1000f;

        // ==================== 输入快照 ====================
        public sealed class Inputs
        {
            public float Load;
            public float FreqGHz;
            public float Voltage;
            public float Current;
            public float? Inlet;
            public float? Hs1;
            public float? Hs2;
        }

        public enum Method
        {
            DualPoint,
            SinglePoint,
            LowEndA,
            LowEndB,
            Linear
        }

        // ==================== 全局修正函数 ====================
        private static double FAtm()
        {
            return Math.Pow(PAtm / 101325.0, 0.25);
        }

        private static double FAge()
        {
            return 1.0 + 0.028 * (1.0 - Math.Exp(-TAgeHours / 1500.0));
        }

        private static double MuAir(double tC)
        {
            double tK = tC + K0;
            return 1.716e-5 * Math.Pow(tK / 273.15, 1.5);
        }

        private static double FVis(double tCase)
        {
            double mu = MuAir(tCase);
            double muRef = MuAir(30.0);
            return Math.Pow(mu / muRef, 0.3);
        }

        private static double RTurb(double gradHs)
        {
            return 1.0 + 0.0035 * Math.Pow(Math.Max(gradHs, 0.0), 0.9);
        }

        private static double FRad(double tHs1, double tInlet)
        {
            double d = tHs1 - tInlet;
            return 1.0 - 0.00012 * d * d;
        }

        private static double FVapor(double tHsG)
        {
            if (tHsG <= 45.0) return 1.0;
            return 1.0 + 0.002 * Math.Pow(tHsG - 45.0, 1.1);
        }

        private static double FVrm(double pg)
        {
            return 1.0 + 0.015 * Math.Pow(pg / 60.0, 1.2);
        }

        private static double LeakHighCpu(double pLeak, double tHotK)
        {
            double d = tHotK - 303.0;
            if (d <= 0) return pLeak;
            return pLeak * (1.0 - 0.0018 * Math.Pow(d, 1.2));
        }

        private static double LeakHighGpu(double pLeak, double tHotK)
        {
            double d = tHotK - 303.0;
            if (d <= 0) return pLeak;
            return pLeak * (1.0 - 0.0022 * Math.Pow(d, 1.25));
        }

        private static double LeakLowCpu(double pLeak, double tHotK)
        {
            double d = 303.0 - tHotK;
            if (d <= 0) return pLeak;
            return pLeak * (1.0 + 0.0025 * Math.Pow(d, 1.1));
        }

        // ==================== 自动方案选择 ====================
        public static Method SelectMethod(Inputs inp)
        {
            bool hasHs2 = inp.Hs2.HasValue && inp.Hs2.Value > 0f;
            bool hasHs1 = inp.Hs1.HasValue && inp.Hs1.Value > 0f;
            bool hasV = inp.Voltage > 0.1f;
            bool hasF = inp.FreqGHz > 0.1f;

            if (hasHs2 && hasHs1 && hasV && hasF) return Method.DualPoint;
            if (hasHs1 && hasV && hasF) return Method.SinglePoint;
            if (hasHs1 && hasV) return Method.LowEndA;
            if (hasV && hasF) return Method.LowEndB;
            return Method.Linear;
        }

        // ==================== CPU 入口 ====================
        public static float EstimateCpu(Inputs inp)
        {
            Normalize(inp);
            var m = SelectMethod(inp);

            float tHot = m switch
            {
                Method.DualPoint => SolveCpuDualPoint(inp),
                Method.SinglePoint => SolveCpuSinglePoint(inp),
                Method.LowEndA => SolveCpuLowEndA(inp),
                Method.LowEndB => SolveCpuLowEndB(inp),
                _ => SolveCpuLinear(inp)
            };

            return ApplyTransientCpu(tHot);
        }

        // ==================== GPU 入口 ====================
        public static float EstimateGpu(Inputs inp, bool isIntegrated, float cpuTemp)
        {
            if (isIntegrated)
            {
                float offset = Math.Clamp(inp.Load * 0.02f, 1f, 4f);
                return Math.Max(cpuTemp - 5f + offset, 20f);
            }

            Normalize(inp);
            var m = SelectMethod(inp);

            float tHot = m switch
            {
                Method.DualPoint => SolveGpuDualPoint(inp),
                Method.SinglePoint => SolveGpuSinglePoint(inp),
                Method.LowEndA => SolveGpuLowEndA(inp),
                Method.LowEndB => SolveGpuLowEndB(inp),
                _ => SolveGpuLinear(inp)
            };

            return ApplyTransientGpu(tHot);
        }

        // ==================== 输入规范化 ====================
        private static void Normalize(Inputs inp)
        {
            if (inp.FreqGHz <= 0.1f)
                inp.FreqGHz = 0.8f + (2.9f - 0.8f) * Math.Min(inp.Load / 100f, 1f);

            if (inp.Voltage <= 0.1f)
                inp.Voltage = Math.Clamp(0.65f + 0.155f * inp.FreqGHz, 0.65f, 1.15f);

            if (!inp.Inlet.HasValue)
                inp.Inlet = TRoom;
        }

        // ==================== 公共功耗计算 ====================
        private static double DynPower(double f, double u, double uth, double kdyn)
        {
            double ueff = Math.Max(u - uth, 0.01);
            return kdyn * f * ueff * u;
        }

        private static double LeakPower(double u, double tHotC, double kleak)
        {
            double tK = tHotC + K0;
            double arg = -Eg / (2.0 * KB * tK);
            double p = kleak * u * Math.Pow(tK, 1.5) * Math.Exp(arg);
            return Math.Clamp(p, 0, 300.0);
        }

        // ====================================================================
        // ================ CPU 参数 ==========================================
        // ====================================================================
        private const double Cpu_Kdyn = 14.5;
        private const double Cpu_Kleak = 9.3e3;
        private const double Cpu_Uth = 0.45;
        private const double Cpu_Kgrad = 0.5;
        private const double Cpu_Alpha = 0.0020;
        private const double Cpu_K1 = 0.30;
        private const double Cpu_K2 = 0.84;
        private const double Cpu_Beta = 0.10;
        private const double Cpu_Gamma = 0.02;
        private const double Cpu_Delta = 0.05;
        private const double CpuA_Eta = 0.012;
        private const double CpuB_Kr = 0.70;
        private const double CpuB_Beta = 0.15;

        // ====================================================================
        // ================ GPU 参数 ==========================================
        // ====================================================================
        private const double Gpu_Kdyn = 12.8;
        private const double Gpu_Kleak = 1.2e4;
        private const double Gpu_Uth = 0.38;
        private const double Gpu_Kgrad = 0.6;
        private const double Gpu_Alpha = 0.0022;
        private const double Gpu_K1 = 0.33;
        private const double Gpu_K2 = 0.82;
        private const double Gpu_Beta = 0.12;
        private const double Gpu_Gamma = 0.025;
        private const double Gpu_Delta = 0.06;
        private const double GpuA_Eta = 0.014;
        private const double GpuB_Kr = 0.75;
        private const double GpuB_Beta = 0.18;

        // ====================================================================
        // ================ CPU 方案实现 ======================================
        // ====================================================================

        private static float SolveCpuDualPoint(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double th1 = inp.Hs1!.Value;
            double th2 = inp.Hs2!.Value;
            double grad = Math.Abs(th1 - th2);

            double fAtm = FAtm();
            double fAge = FAge();
            double rTurb = RTurb(grad);
            double fRad = FRad(th1, tInlet);
            double tHot = th1 + 15.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Cpu_Uth, Cpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Cpu_Kleak);
                double pLeakHigh = LeakHighCpu(pLeak, tK);
                double pEst = pDyn + pLeakHigh;

                double rGrad = Cpu_Kgrad * grad / (pEst + 1e-6);
                double fVis = FVis(th1 - 10);
                double rTh = (th1 - tInlet) / (pEst + 1e-6)
                             * (1 + Cpu_Alpha * (th1 - Tref))
                             * (1 + Cpu_Gamma * rGrad)
                             * rTurb * fRad * fAtm * fAge * fVis;

                double dTChip = Cpu_K1 * Math.Pow(pEst, Cpu_K2)
                                * (1 - Cpu_Beta * (th1 - tInlet) / tK)
                                * (1 + Cpu_Delta * Math.Pow(pEst, 0.3))
                                * (1 + 0.005 * Math.Pow(pEst, 0.8));

                double tNew = th1 + dTChip;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveCpuSinglePoint(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double th = inp.Hs1!.Value;
            double fAtm = FAtm();
            double fAge = FAge();
            double tHot = th + 15.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Cpu_Uth, Cpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Cpu_Kleak);
                double pLeakHigh = LeakHighCpu(pLeak, tK);
                double pEst = pDyn + pLeakHigh;

                double fVis = FVis(th - 10);
                double rTh = (th - tInlet) / (pEst + 1e-6)
                             * (1 + Cpu_Alpha * (th - Tref))
                             * fAtm * fAge * fVis;

                double dTChip = Cpu_K1 * Math.Pow(pEst, Cpu_K2)
                                * (1 - Cpu_Beta * (th - tInlet) / tK)
                                * (1 + Cpu_Delta * Math.Pow(pEst, 0.3))
                                * (1 + 0.005 * Math.Pow(pEst, 0.8));

                double tNew = th + dTChip;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveCpuLowEndA(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double th = inp.Hs1!.Value;
            double fAtm = FAtm();
            double fAge = FAge();
            double tHot = th + 15.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Cpu_Uth, Cpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Cpu_Kleak);
                double pLeakLow = LeakLowCpu(pLeak, tK);
                double pEst = pDyn + pLeakLow;

                double rTh = (th - tInlet) / (pEst + 1e-6)
                             * (1 + Cpu_Alpha * (th - 30))
                             * (1 + CpuA_Eta * Math.Pow(pEst, 0.2))
                             * fAge * fAtm;

                double dTChip = Cpu_K1 * Math.Pow(pEst, Cpu_K2)
                                * (1 - Cpu_Beta * (th - tInlet) / tK)
                                * (1 + 0.01 * Math.Pow(Math.Max(40 - tInlet, 1), 0.7));

                double tNew = th + dTChip;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveCpuLowEndB(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double fAtm = FAtm();
            double fAge = FAge();
            double tHot = tInlet + 20.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Cpu_Uth, Cpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Cpu_Kleak);
                double pEst = pDyn + pLeak;

                double dTotal = CpuB_Kr * Math.Pow(pEst, 0.92)
                                * (1 - CpuB_Beta * pEst / (pEst + 80.0))
                                * (1 + 0.004 * Math.Pow(pEst, 0.95))
                                * fAge * fAtm;

                double tNew = tInlet + dTotal;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveCpuLinear(Inputs inp)
        {
            return 30f + inp.Load * 0.45f;
        }

        // ====================================================================
        // ================ GPU 方案实现 ======================================
        // ====================================================================

        private static float SolveGpuDualPoint(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double th1 = inp.Hs1!.Value;
            double th2 = inp.Hs2!.Value;
            double grad = Math.Abs(th1 - th2);
            double fAtm = FAtm();
            double fAge = FAge();
            double rTurb = RTurb(grad);
            double fRad = FRad(th1, tInlet);
            double tHot = th1 + 12.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Gpu_Uth, Gpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Gpu_Kleak);
                double pLeakHigh = LeakHighGpu(pLeak, tK);
                double pEst = pDyn + pLeakHigh;

                double fVis = FVis(th1 - 10);
                double fVapor = FVapor(th1);
                double rGrad = Gpu_Kgrad * grad / (pEst + 1e-6);
                double rTh = (th1 - tInlet) / (pEst + 1e-6)
                             * (1 + Gpu_Alpha * (th1 - Tref))
                             * (1 + Gpu_Gamma * rGrad)
                             * rTurb * fRad * fAtm * fAge * fVis * fVapor;

                double dTChip = Gpu_K1 * Math.Pow(pEst, Gpu_K2)
                                * (1 - Gpu_Beta * (th1 - tInlet) / tK)
                                * (1 + Gpu_Delta * Math.Pow(pEst, 0.3))
                                * (1 + 0.006 * Math.Pow(pEst, 0.85));

                double tNew = th1 + dTChip;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveGpuSinglePoint(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double th = inp.Hs1!.Value;
            double fAtm = FAtm();
            double fAge = FAge();
            double tHot = th + 12.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Gpu_Uth, Gpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Gpu_Kleak);
                double pLeakHigh = LeakHighGpu(pLeak, tK);
                double pEst = pDyn + pLeakHigh;

                double fVis = FVis(th - 10);
                double fVapor = FVapor(th);
                double rTh = (th - tInlet) / (pEst + 1e-6)
                             * (1 + Gpu_Alpha * (th - Tref))
                             * fAtm * fAge * fVis * fVapor;

                double dTChip = Gpu_K1 * Math.Pow(pEst, Gpu_K2)
                                * (1 - Gpu_Beta * (th - tInlet) / tK)
                                * (1 + Gpu_Delta * Math.Pow(pEst, 0.3))
                                * (1 + 0.006 * Math.Pow(pEst, 0.85));

                double tNew = th + dTChip;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveGpuLowEndA(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double th = inp.Hs1!.Value;
            double fAtm = FAtm();
            double fAge = FAge();
            double tHot = th + 12.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Gpu_Uth, Gpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Gpu_Kleak);
                double pEst = pDyn + pLeak;

                double fVapor = FVapor(th);
                double rTh = (th - tInlet) / (pEst + 1e-6)
                             * (1 + Gpu_Alpha * (th - 30))
                             * (1 + GpuA_Eta * Math.Pow(pEst, 0.2))
                             * (1 + 0.02 * Math.Exp(-pEst / 40.0))
                             * fAge * fAtm * fVapor;

                double dTChip = Gpu_K1 * Math.Pow(pEst, Gpu_K2)
                                * (1 - Gpu_Beta * (th - tInlet) / tK);

                double tNew = th + dTChip;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveGpuLowEndB(Inputs inp)
        {
            double tInlet = inp.Inlet!.Value;
            double fAtm = FAtm();
            double fAge = FAge();
            double tHot = tInlet + 20.0;

            for (int i = 0; i < MaxIter; i++)
            {
                double tK = tHot + K0;
                double pDyn = DynPower(inp.FreqGHz, inp.Voltage, Gpu_Uth, Gpu_Kdyn);
                double pLeak = LeakPower(inp.Voltage, tHot, Gpu_Kleak);
                double pEst = pDyn + pLeak;

                double dTotal = GpuB_Kr * Math.Pow(pEst, 0.92)
                                * (1 - GpuB_Beta * pEst / (pEst + 120.0))
                                * (1 + 0.005 * Math.Pow(pEst, 1.0))
                                * fAge * fAtm;

                double tNew = tInlet + dTotal;
                if (Math.Abs(tNew - tHot) < ConvEps) { tHot = tNew; break; }
                tHot = tNew;
            }
            return (float)tHot;
        }

        private static float SolveGpuLinear(Inputs inp)
        {
            return 35f + inp.Load * 0.40f;
        }

        // ====================================================================
        // ================ 瞬态热惯性修正 ====================================
        // ====================================================================
        private static float _cpuLastT = -1f;
        private static DateTime _cpuLastTTime = DateTime.MinValue;
        private const float CpuTau = 40f;

        private static float _gpuLastT = -1f;
        private static DateTime _gpuLastTTime = DateTime.MinValue;
        private const float GpuTau = 30f;

        private static float ApplyTransientCpu(float target)
        {
            var now = DateTime.Now;
            if (_cpuLastT < 0f || _cpuLastTTime == DateTime.MinValue)
            {
                _cpuLastT = target;
                _cpuLastTTime = now;
                return target;
            }

            double dt = (now - _cpuLastTTime).TotalSeconds;
            _cpuLastTTime = now;
            if (dt <= 0 || dt > 30) { _cpuLastT = target; return target; }

            double alpha = 1.0 - Math.Exp(-dt / CpuTau);
            _cpuLastT = (float)(_cpuLastT + (target - _cpuLastT) * alpha);
            return _cpuLastT;
        }

        private static float ApplyTransientGpu(float target)
        {
            var now = DateTime.Now;
            if (_gpuLastT < 0f || _gpuLastTTime == DateTime.MinValue)
            {
                _gpuLastT = target;
                _gpuLastTTime = now;
                return target;
            }

            double dt = (now - _gpuLastTTime).TotalSeconds;
            _gpuLastTTime = now;
            if (dt <= 0 || dt > 30) { _gpuLastT = target; return target; }

            double alpha = 1.0 - Math.Exp(-dt / GpuTau);
            _gpuLastT = (float)(_gpuLastT + (target - _gpuLastT) * alpha);
            return _gpuLastT;
        }

        // ==================== 外部接口 ====================
        public static void SetRoomTemp(float t) => TRoom = Math.Clamp(t, -20f, 50f);
        public static void SetPressure(float p) => PAtm = Math.Clamp(p, 50000f, 110000f);
        public static void SetAgeHours(float h) => TAgeHours = Math.Clamp(h, 0f, 200000f);
        public static void ResetCpu() { _cpuLastT = -1f; _cpuLastTTime = DateTime.MinValue; }
        public static void ResetGpu() { _gpuLastT = -1f; _gpuLastTTime = DateTime.MinValue; }
    }
}