using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class SystemHardwarPage : SettingsPageBase
    {
        private Panel _container;
        
        // ★★★ 修复：类型更正为 LiteComboBox ★★★
        private LiteComboBox _cbDisk, _cbNet, _cbGpu, _cbMobo;
        private LiteComboBox _cbFanCpu, _cbFanPump, _cbFanCase;

        public SystemHardwarPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);
            _container = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20) }; 
            this.Controls.Add(_container);

            InitializeUI();
        }

        private void InitializeUI()
        {
            CreateSourceCard();
            CreateCalibrationCard();
            CreateSystemCard();
        }

        public override void OnShow()
        {
            base.OnShow();
            if (Config == null) return;
            PopulateAsyncData();
        }

        // 将 PopulateAsyncData 改为“批量处理”模式
        private async void PopulateAsyncData()
{
    try
    {
        string strAuto = LanguageManager.T("Menu.Auto");

        // ★ 关键优化：一次性从服务读所有硬件列表，避免 5 个线程同时抢管道
        await Task.Run(() =>
        {
            // 触发一次服务读取，结果缓存在 _hardwareList
            HardwareMonitor.ListAllGpus();
        });

        // 现在从缓存读，0 延迟
        var disks = HardwareMonitor.ListAllDisks();
        var nets = HardwareMonitor.ListAllNetworks();
        var gpus = HardwareMonitor.ListAllGpuOptions();
        var fans = HardwareMonitor.ListAllFans();
        var mobos = HardwareMonitor.ListAllMoboTemps();

        this.SuspendLayout();

        void FillSync(LiteComboBox combo, List<string> data, string currentVal)
        {
            if (combo == null || combo.Inner.Items.Count > 2) return;

            var fullList = new List<string>(data);
            fullList.Insert(0, strAuto);

            combo.Inner.BeginUpdate();
            combo.Inner.Items.Clear();
            foreach (var item in fullList) combo.Inner.Items.Add(item);

            if (!string.IsNullOrEmpty(currentVal) && fullList.Contains(currentVal))
                combo.Inner.SelectedItem = currentVal;
            else
                combo.Inner.SelectedIndex = 0;

            combo.Inner.EndUpdate();
        }

        void FillGpuSync(LiteComboBox combo, List<HardwareScanner.GpuOption> data, string currentVal)
        {
            if (combo == null || combo.Inner.Items.Count > 2) return;

            HardwareScanner.GpuOption? ResolveCurrent()
            {
                if (string.IsNullOrWhiteSpace(currentVal)) return null;

                var byValue = data.FirstOrDefault(x =>
                    string.Equals(x.Value, currentVal, StringComparison.OrdinalIgnoreCase));
                if (byValue != null) return byValue;

                var byName = data.Where(x =>
                    string.Equals(x.Name, currentVal, StringComparison.OrdinalIgnoreCase)).ToList();
                return byName.Count == 1 ? byName[0] : null;
            }

            combo.Inner.BeginUpdate();
            combo.Inner.Items.Clear();
            combo.AddItem(strAuto, "");
            foreach (var item in data) combo.AddItem(item.Label, item.Value);

            var selected = ResolveCurrent();
            combo.SelectValue(selected?.Value ?? "");
            combo.Inner.EndUpdate();
        }

        FillSync(_cbDisk, disks, Config.PreferredDisk);
        FillSync(_cbNet, nets, Config.PreferredNetwork);
        FillGpuSync(_cbGpu, gpus, Config.PreferredGpu);
        FillSync(_cbMobo, mobos, Config.PreferredMoboTemp);
        FillSync(_cbFanCpu, fans, Config.PreferredCpuFan);
        FillSync(_cbFanPump, fans, Config.PreferredCpuPump);
        FillSync(_cbFanCase, fans, Config.PreferredCaseFan);
    }
    catch (Exception ex)
    {
        Console.WriteLine("硬件列表加载失败: " + ex.Message);
    }
    finally
    {
        this.ResumeLayout(true);
    }
}

        private void CreateSourceCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.HardwareSettings"));
            string strAuto = LanguageManager.T("Menu.Auto");
            
            group.AddToggle(this, "Menu.UseWinPerCounters", () => Config?.UseWinPerCounters ?? false, v => { if(Config!=null) Config.UseWinPerCounters = v; });
            
            // ★★★ [新增] 忽略 SMB 流量开关 ★★★
            // 直接使用中文作为 Key，如果 LanguageManager 找不到 Key 会原样返回
            group.AddToggle(this, LanguageManager.T("Menu.IgnoreSMBTraffic"),
                () => Config?.IgnoreSmbTraffic ?? false, 
                v => { if(Config!=null) Config.IgnoreSmbTraffic = v; }
            );

            // 内存/显存显示模式 (从主界面设置移来)
            string[] memOptions = { LanguageManager.T("Menu.Percent"), LanguageManager.T("Menu.UsedSize") };
            group.AddComboIndex(this, "Menu.MemoryDisplayMode", memOptions,
                () => Config?.MemoryDisplayMode ?? 0,
                idx => { if (Config != null) Config.MemoryDisplayMode = idx; }
            );

            // ============================================================
            // 刷新频率选项：从小到大排序，最后放"与资源管理器一致"
            // ============================================================
            const string SyncLabel = "与资源管理器一致";
            var refreshList = new List<string>
            { 
             "100 ms",
             "200 ms",
             "300 ms",
             "400 ms",
             "500 ms",
             "600 ms",
             "700 ms",
             "800 ms",
             "900 ms",
             "1000 ms",
             "1500 ms",
             "2000 ms",
             "2500 ms",
             "3000 ms",
             SyncLabel
         };

group.AddCombo(this, "Menu.Refresh", refreshList,
    () =>
    {
        int ms = Config?.RefreshMs ?? 1000;
        return ms == -1 ? SyncLabel : ms + " ms";
    },
    v =>
    {
        if (Config == null) return;
        if (v == SyncLabel)
            Config.RefreshMs = -1;   // -1 = 与资源管理器一致
        else
            Config.RefreshMs = MetricUtils.ParseInt(v);
    }
);

            // ★★★ 修复：强制转换为 LiteComboBox ★★★
            _cbDisk = (LiteComboBox)group.AddCombo(this, "Menu.DiskSource", new List<string> { strAuto }, 
                () => Config?.PreferredDisk ?? strAuto, 
                v => { if(Config!=null) Config.PreferredDisk = (v == strAuto ? "" : v); });

            _cbNet = (LiteComboBox)group.AddCombo(this, "Menu.NetworkSource", new List<string> { strAuto },
                () => Config?.PreferredNetwork ?? strAuto,
                v => { if (Config != null) Config.PreferredNetwork = (v == strAuto ? "" : v); });

            _cbGpu = (LiteComboBox)group.AddComboPair(this, LanguageManager.T("Menu.GpuSource"),
                new[] { new { Label = strAuto, Value = "" } },
                () => Config?.PreferredGpu ?? "",
                v => { if (Config != null) Config.PreferredGpu = v ?? ""; });

            _cbMobo = (LiteComboBox)group.AddCombo(this, "Items.MOBO.Temp", new List<string> { strAuto },
                () => Config?.PreferredMoboTemp ?? strAuto, v => { if (Config != null) Config.PreferredMoboTemp = (v == strAuto ? "" : v); });

            _cbFanCpu = (LiteComboBox)group.AddCombo(this, "Items.CPU.Fan", new List<string> { strAuto },
                () => Config?.PreferredCpuFan ?? strAuto, v => { if (Config != null) Config.PreferredCpuFan = (v == strAuto ? "" : v); });
            
            _cbFanPump = (LiteComboBox)group.AddCombo(this, "Items.CPU.Pump", new List<string> { strAuto },
                () => Config?.PreferredCpuPump ?? strAuto, v => { if (Config != null) Config.PreferredCpuPump = (v == strAuto ? "" : v); });

            _cbFanCase = (LiteComboBox)group.AddCombo(this, "Items.CASE.Fan", new List<string> { strAuto },
                () => Config?.PreferredCaseFan ?? strAuto, v => { if (Config != null) Config.PreferredCaseFan = (v == strAuto ? "" : v); });

            AddGroupToPage(group);
        }

        private void CreateCalibrationCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.Calibration"));
            string suffix = " (" + LanguageManager.T("Menu.MaxLimits") + ")";

            void AddCalib(string key, string unit, Func<float> get, Action<float> set)
            {
                var input = group.AddDouble(this, "RAW_TITLE_HACK", unit, 
                    () => (int)(get?.Invoke() ?? 0),        
                    v => set?.Invoke((float)(int)v)
                );
                if(input.Parent.Controls[0] is Label lbl) lbl.Text = LanguageManager.T(key) + suffix; 
            }
            
            group.AddHint(LanguageManager.T("Menu.CalibrationTip"));
            AddCalib("Items.CPU.Power", "W",   () => Config?.RecordedMaxCpuPower ?? 100, v => { if(Config!=null) Config.RecordedMaxCpuPower = v; });
            AddCalib("Items.CPU.Clock", "MHz", () => Config?.RecordedMaxCpuClock ?? 5000, v => { if(Config!=null) Config.RecordedMaxCpuClock = v; });
            AddCalib("Items.GPU.Power", "W",   () => Config?.RecordedMaxGpuPower ?? 300, v => { if(Config!=null) Config.RecordedMaxGpuPower = v; });
            AddCalib("Items.GPU.Clock", "MHz", () => Config?.RecordedMaxGpuClock ?? 2000, v => { if(Config!=null) Config.RecordedMaxGpuClock = v; });
            AddCalib("Items.CPU.Fan",   "RPM", () => Config?.RecordedMaxCpuFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxCpuFan = v; });
            AddCalib("Items.CPU.Pump",  "RPM", () => Config?.RecordedMaxCpuPump ?? 2000, v => { if(Config!=null) Config.RecordedMaxCpuPump = v; });
            AddCalib("Items.GPU.Fan",   "RPM", () => Config?.RecordedMaxGpuFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxGpuFan = v; });
            AddCalib("Items.CASE.Fan",  "RPM", () => Config?.RecordedMaxChassisFan ?? 2000, v => { if(Config!=null) Config.RecordedMaxChassisFan = v; });

            AddGroupToPage(group);
        }

        private void CreateSystemCard()
        {
            var group = new LiteSettingsGroup(LanguageManager.T("Menu.SystemSettings"));
            
            var langs = new List<string>();
            string langDir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
            if (Directory.Exists(langDir))
                langs.AddRange(Directory.EnumerateFiles(langDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f).ToUpper()));
            
            group.AddCombo(this, "Menu.Language", langs,
                () => string.IsNullOrEmpty(Config?.Language) ? LanguageManager.CurrentLang.ToUpper() : Config.Language.ToUpper(),
                v => { if(Config!=null) Config.Language = v.ToLower(); }
            );

            group.AddToggle(this, "Menu.AutoStart", () => Config?.AutoStart ?? false, v => { if(Config!=null) Config.AutoStart = v; });
            group.AddToggle(this, "Menu.AutoCheckUpdate", () => Config?.AutoCheckUpdate ?? true, v => { if(Config!=null) Config.AutoCheckUpdate = v; });

            var chkTray = group.AddToggle(this, "Menu.HideTrayIcon", 
                () => Config?.HideTrayIcon ?? false, 
                v => { if(Config!=null) Config.HideTrayIcon = v; });
            chkTray.CheckedChanged += (s, e) => { if(Config!=null) EnsureSafeVisibility(null, chkTray, null); };

            AddGroupToPage(group);
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 20) };
            wrapper.Controls.Add(group);
            _container.Controls.Add(wrapper);
            _container.Controls.SetChildIndex(wrapper, 0);
        }
    }
}
