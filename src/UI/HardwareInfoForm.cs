using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using LiteMonitor;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI
{
    public class HardwareInfoForm : Form
    {
        private LiteTreeView _tree;
        private System.Windows.Forms.Timer _refreshTimer;
        private Panel _headerPanel;
         private TextBox _searchInput;
        
        private Settings _settings = Settings.Load();
        
        private string T(string en, string zh) => _settings.Language.ToLower().StartsWith("zh") ? zh : en; 

        public HardwareInfoForm()
        {
            this.Text = T("LiteMonitor - Hardware Info", "LiteMonitor - 系统硬件详情");
            this.Size = new Size(UIUtils.S(600), UIUtils.S(750));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;
            this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            // 搜索栏
            var pnlToolbar = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(40), Padding = new Padding(10), BackColor = Color.WhiteSmoke };
            _searchInput = new TextBox { 
                Dock = DockStyle.Fill, 
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 9f), 
                PlaceholderText = T("Search sensor name...", "搜索传感器名称...") 
            };
            _searchInput.TextChanged += (s, e) => RebuildTree(_searchInput.Text.Trim());
            pnlToolbar.Controls.Add(_searchInput);

            // 表头
            _headerPanel = new Panel { Dock = DockStyle.Top, Height = UIUtils.S(24), BackColor = Color.FromArgb(250, 250, 250) };
            _headerPanel.Paint += HeaderPanel_Paint;
            _headerPanel.Resize += (s, e) => _headerPanel.Invalidate();

            _tree = new LiteTreeView { Dock = DockStyle.Fill };
            
            var cms = new ContextMenuStrip();
            
            var itemCopyName = cms.Items.Add(T("Copy Name", "复制名称"), null, (s, e) => CopyInfo("Name"));
            var itemCopyId = cms.Items.Add(T("Copy ID", "复制传感器ID"), null, (s, e) => CopyInfo("ID"));
            var itemCopyVal = cms.Items.Add(T("Copy Value", "复制数值"), null, (s, e) => CopyInfo("Value"));
            
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add(T("Expand All", "全部展开"), null, (s, e) => _tree.ExpandAll());
            cms.Items.Add(T("Collapse All", "全部折叠"), null, (s, e) => _tree.CollapseAll());

            cms.Opening += (s, e) => 
            {
                var node = _tree.SelectedNode;
                if (node == null)
                {
                    e.Cancel = true;
                    return;
                }

                bool isSensor = node.Tag is ISensor;
                
                itemCopyName.Visible = true;
                itemCopyId.Visible = isSensor;
                itemCopyVal.Visible = isSensor;
            };

            _tree.ContextMenuStrip = cms;

            this.Controls.Add(_tree);
            this.Controls.Add(_headerPanel);
            this.Controls.Add(pnlToolbar);

            RebuildTree("");

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (s, e) => UpdateVisibleNodesSmart();
            _refreshTimer.Start();
        }

        private void UpdateVisibleNodesSmart()
        {
            if (!this.Visible || _tree.IsDisposed) return;
            TreeNode node = _tree.TopNode;
            while (node != null)
            {
                if (node.Bounds.Top > _tree.ClientSize.Height) break;
                if (node.Tag is ISensor) _tree.InvalidateSensorValue(node);
                node = node.NextVisibleNode;
            }
        }

        private void HeaderPanel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            
            int w = _headerPanel.ClientSize.Width; 

            using (var pen = new Pen(Color.FromArgb(230, 230, 230)))
                g.DrawLine(pen, 0, _headerPanel.Height - 1, w, _headerPanel.Height - 1);

            var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold); 
            
            int rightMargin = UIUtils.S(_tree.RightMargin);
            int iconWidth = UIUtils.S(_tree.IconWidth);
            int colMaxW = UIUtils.S(_tree.ColMaxWidth);
            int colValW = UIUtils.S(_tree.ColValueWidth);
            int gap = UIUtils.S(10);

            int xIconLeft = w - rightMargin - iconWidth;
            int xMaxLeft = xIconLeft - gap - colMaxW-20;
            int xValueLeft = xMaxLeft - gap - colValW;

            Rectangle titleRect = new Rectangle(30, 0, xValueLeft - 10, _headerPanel.Height);
            TextRenderer.DrawText(g, " " + T("Sensor", "硬件 > 传感器"), font, titleRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            Rectangle maxRect = new Rectangle(xMaxLeft, 0, colMaxW, _headerPanel.Height);
            TextRenderer.DrawText(g, T("Max", "最大记录"), font, maxRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);

            Rectangle valRect = new Rectangle(xValueLeft, 0, colValW, _headerPanel.Height);
            TextRenderer.DrawText(g, T("Value", "数值"), font, valRect, Color.FromArgb(80, 80, 80), 
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);
            
            font.Dispose();
        }

        private void RebuildTree(string filter)
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            var computer = HardwareMonitor.Instance?.ComputerInstance;
            if (computer == null || computer.Hardware.Count == 0) 
            {
                _tree.Nodes.Add(new TreeNode(T("Initializing...", "初始化中...")));
                _tree.EndUpdate();
                return;
            }

            bool isFirstHardware = true;
            foreach (var hw in computer.Hardware)
            {
                AddHardwareNode(_tree.Nodes, hw, filter, !string.IsNullOrEmpty(filter), isFirstHardware && string.IsNullOrEmpty(filter));
                isFirstHardware = false;
            }
            _tree.EndUpdate();
        }

        private void AddHardwareNode(TreeNodeCollection parentNodes, IHardware hw, string filter, bool isSearch, bool isFirstHardware)
        {
            string typeStr = GetHardwareTypeString(hw.HardwareType);
            string cleanName = SanitizeHardwareName(hw.Name);
            string label = $"{typeStr} {cleanName}";

            var hwNode = new TreeNode(label) { Tag = hw };
            bool hasContent = false;

            var groups = hw.Sensors.GroupBy(s => s.SensorType).OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                string typeIcon = GetSensorTypeString(group.Key);
                string typeName = $"{typeIcon} {group.Key}"; 
                
                var typeNode = new TreeNode(typeName) { Tag = group.Key };

                bool groupHasMatch = false;
                foreach (var s in group)
                {
                    if (isSearch && !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !hw.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    typeNode.Nodes.Add(new TreeNode(s.Name) { Tag = s });
                    groupHasMatch = true;
                }

                if (groupHasMatch)
                {
                    hwNode.Nodes.Add(typeNode);
                    if (isSearch) typeNode.Expand();
                    hasContent = true;
                }
            }

            foreach (var subHw in hw.SubHardware)
            {
                AddHardwareNode(hwNode.Nodes, subHw, filter, isSearch, isFirstHardware);
            }
            if (hwNode.Nodes.Count > 0) hasContent = true;

            if (!isSearch || hasContent || hw.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                parentNodes.Add(hwNode);
                
                if (isSearch)
                {
                    hwNode.Expand();
                }
                else
                {
                    if (isFirstHardware)
                    {
                        hwNode.Expand();
                    }
                }
            }
        }

        private void CopyInfo(string type)
        {
            var node = _tree.SelectedNode;
            if (node == null) return;

            if (type == "Name")
            {
                if (node.Tag is IHardware hw)
                {
                    Clipboard.SetText(SanitizeHardwareName(hw.Name));
                }
                else if (node.Tag is ISensor s)
                {
                    Clipboard.SetText(s.Name ?? "");
                }
                else if (node.Tag is SensorType st)
                {
                    Clipboard.SetText(st.ToString()); 
                }
                else
                {
                    Clipboard.SetText(node.Text ?? "");
                }
            }
            else if (node.Tag is ISensor s)
            {
                if (type == "Value") Clipboard.SetText(s.Value?.ToString() ?? "");
                else if (type == "ID") Clipboard.SetText(s.Identifier.ToString());
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            base.OnFormClosed(e);
            this.Dispose();
        }

        private string GetHardwareTypeString(HardwareType type)
        {
            switch (type) {
                case HardwareType.Cpu: return T("💻 [CPU]", "💻 [处理器]");
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel: return T("🎮 [GPU]", "🎮 [显卡]");
                case HardwareType.Memory: return T("💾 [Memory]", "💾 [内存]");
                case HardwareType.Motherboard: return T("⌨ [Motherboard]", "⌨ [主板]");
                case HardwareType.Storage: return T("💽 [Storage]", "💽 [硬盘]");
                case HardwareType.Network: return T("🌐 [Network]", "🌐 [网卡]"); 
                case HardwareType.SuperIO: return T("📟 [SuperIO]", "📟 [IO芯片]");
                case HardwareType.Cooler: return T("❄️ [Cooler]", "❄️ [散热器]");
                default: return $"🟢 [{type}]";
            }
        }
        private string GetSensorTypeString(SensorType type)
        {
            switch (type) {
                case SensorType.Temperature: return T("🌡️ [Temperature]", "🌡️ [温度]");
                case SensorType.Load: return T("⌛ [Load]", "⌛ [负载]");
                case SensorType.Fan: return T("🌀 [Fan]", "🌀 [风扇]");
                case SensorType.Power: return T("⚡ [Power]", "⚡ [功耗]");
                case SensorType.Clock: return T("⏱️ [Clock]", "⏱️ [频率]");
                case SensorType.Control: return T("🎛️ [Control]", "🎛️ [控制]");
                case SensorType.Voltage: return T("🔋 [Voltage]", "🔋 [电压]");
                case SensorType.Data: return T("📈 [Data]", "📈 [数据]");
                case SensorType.SmallData: return T("📶 [SmallData]", "📶 [小型数据]");
                case SensorType.Throughput: return T("🚀 [Throughput]", "🚀 [吞吐量]");
                case SensorType.Level: return T("📉 [Level]", "📉 [剩余/寿命]");
                case SensorType.Factor: return T("🔢 [Factor]", "🔢 [系数]");
                case SensorType.Timing: return T("⏱️ [Timing]", "⏱️ [时序]");
                default: return $"🟢 [{type}]";
            }
        }

        private string SanitizeHardwareName(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            char[] cleanChars = input.Where(c => 
                char.IsLetterOrDigit(c) || 
                c == ' ' || c == '-' || c == '_' || c == '.' || 
                c == '(' || c == ')' || c == '[' || c == ']' ||
                c == '#' || c == '/' || c == '+'
            ).ToArray();

            string result = new string(cleanChars).Trim();

            if (result.Length < 2) return "Generic Hardware"; 

            while (result.Contains("  ")) result = result.Replace("  ", " ");

            return result;
        }
    }
}