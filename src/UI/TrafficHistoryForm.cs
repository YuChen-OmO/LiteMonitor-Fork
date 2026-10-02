using System.Globalization;
using System.Reflection; // 用于给 DataGridView 开缓冲
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public enum HistoryViewMode { Daily, Weekly, Monthly, Quarterly, Yearly }

    public class TrafficHistoryForm : Form
    {
        private readonly Settings _cfg;
        private readonly System.Windows.Forms.Timer _timer;

        private Label _lblSessionUp, _lblSessionDown;
        private Label _lblTodayUp, _lblTodayDown;
        private Label _lblListSummary;
        private DataGridView _grid;
        private Dictionary<HistoryViewMode, Button> _viewButtons = new Dictionary<HistoryViewMode, Button>();

        private HistoryViewMode _currentMode = HistoryViewMode.Daily;

        private float _scale = 1.0f;

        // 参考 SettingsForm，使用 WS_EX_COMPOSITED 消除首次打开闪烁
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
                return cp;
            }
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            this.SuspendLayout();
            base.OnResizeBegin(e);
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            this.ResumeLayout(true);
        }

        // === 配色 ===
        private readonly Color C_Back = Color.FromArgb(32, 32, 32);
        private readonly Color C_Panel = Color.FromArgb(45, 45, 45);
        private readonly Color C_GridBack = Color.FromArgb(38, 38, 38);
        private readonly Color C_GridLine = Color.FromArgb(60, 60, 60);
        private readonly Color C_TextMain = Color.FromArgb(230, 230, 230);
        private readonly Color C_TextDim = Color.FromArgb(160, 160, 160);
        private readonly Color C_Header = Color.FromArgb(50, 50, 50);
        private readonly Color C_Accent = Color.FromArgb(0, 122, 204);

        private readonly Color C_Up = Color.FromArgb(80, 160, 255);
        private readonly Color C_Down = Color.FromArgb(80, 220, 120);
        private readonly Color C_BarBg = Color.FromArgb(60, 60, 60);

        private readonly Color C_Highlight = Color.FromArgb(255, 215, 0);

        public TrafficHistoryForm(Settings cfg)
        {
            _cfg = cfg;
            // 【修改点 1】开启窗体自身的双缓冲（解决背景白屏）
            this.DoubleBuffered = true;

            using (Graphics g = this.CreateGraphics())
            {
                _scale = g.DpiX / 96.0f;
            }

            this.Text = "流量统计中心 (Traffic Statistics)";
            this.Size = new Size(S(840), S(650));
            this.MinimumSize = new Size(S(840), S(650));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = C_Back;
            this.ForeColor = C_TextMain;
            this.Font = new Font("Microsoft YaHei UI", 9F);

            // 【修改点 2】在 InitializeUI 前后加上挂起和恢复布局
            // 这会让系统知道：“我还没摆好控件，先别画，等我摆完了再一次性画出来”
            this.SuspendLayout(); 
            InitializeUI();
            this.ResumeLayout(true); // 参数 true 也就是立即执行布局逻辑

            SwitchView(HistoryViewMode.Daily);
            UpdateRealtimeStats();

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, __) => UpdateRealtimeStats();
            _timer.Start();
        }

        private int S(int pixel) => (int)(pixel * _scale);

        private void InitializeUI()
        {
            // === 1. 顶部仪表盘 ===
            var pnlDash = new Panel
            {
                Dock = DockStyle.Top,
                Height = S(120),
                BackColor = C_Panel,
            };

            CreateDirectDash(pnlDash, S(50), "本次运行 (Current Session)", out _lblSessionUp, out _lblSessionDown);

            var split = new Label
            {
                AutoSize = false,
                Size = new Size(1, S(80)),
                Location = new Point(S(400), S(25)),
                BackColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            pnlDash.Controls.Add(split);

            CreateDirectDash(pnlDash, S(450), "今日汇总 (Today Total)", out _lblTodayUp, out _lblTodayDown);

            this.Controls.Add(pnlDash);

            // === 2. 视图切换栏 ===
            var pnlTool = new Panel { Dock = DockStyle.Top, Height = S(55), BackColor = C_Back };
            pnlTool.Controls.Add(new Label
            {
                Text = "统计维度 (View):",
                ForeColor = C_TextDim,
                Location = new Point(S(20), S(18)),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9)
            });

            int btnX = S(140);
            _viewButtons.Add(HistoryViewMode.Daily, CreateBtn(pnlTool, "日报 (Day)", ref btnX));
            _viewButtons.Add(HistoryViewMode.Weekly, CreateBtn(pnlTool, "周报 (Week)", ref btnX));
            _viewButtons.Add(HistoryViewMode.Monthly, CreateBtn(pnlTool, "月报 (Month)", ref btnX));
            _viewButtons.Add(HistoryViewMode.Quarterly, CreateBtn(pnlTool, "季报 (Quarter)", ref btnX));
            _viewButtons.Add(HistoryViewMode.Yearly, CreateBtn(pnlTool, "年报 (Year)", ref btnX));
            this.Controls.Add(pnlTool);

            // === 3. 底部汇总栏 ===
            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = S(45), BackColor = C_Panel };
            _lblListSummary = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = C_TextMain,
                Font = new Font("Consolas", 11, FontStyle.Bold),
                Padding = new Padding(0, 0, S(25), 0),
                Text = "Loading..."
            };
            pnlBottom.Controls.Add(_lblListSummary);
            this.Controls.Add(pnlBottom);

            // === 4. 列表 ===
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = C_GridBack,
                BorderStyle = BorderStyle.None,
                EnableHeadersVisualStyles = false,
                GridColor = C_GridLine,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersHeight = S(45),
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            _grid.ColumnHeadersDefaultCellStyle.BackColor = C_Header;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Gainsboro;
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular);
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;

            _grid.DefaultCellStyle.BackColor = C_GridBack;
            _grid.DefaultCellStyle.ForeColor = C_TextMain;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(70, 70, 70);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.Font = new Font("Consolas", 11);
            _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.RowTemplate.Height = S(36);

            // 添加列，并显式指定 SortMode 为 Automatic
            // Tag结构: [0]=Total, [1]=Max, [2]=Up, [3]=Down
            var colDate = new DataGridViewTextBoxColumn { Name = "Date", HeaderText = "时间段 (Period)", FillWeight = 90, SortMode = DataGridViewColumnSortMode.Automatic };
            colDate.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _grid.Columns.Add(colDate);

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Up", HeaderText = "上传 (Up)", FillWeight = 80, SortMode = DataGridViewColumnSortMode.Automatic });
            _grid.Columns["Up"].DefaultCellStyle.ForeColor = C_Up;

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Down", HeaderText = "下载 (Down)", FillWeight = 80, SortMode = DataGridViewColumnSortMode.Automatic });
            _grid.Columns["Down"].DefaultCellStyle.ForeColor = C_Down;

            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Total", HeaderText = "总计 (Total)", FillWeight = 80, SortMode = DataGridViewColumnSortMode.Automatic });

            _grid.Columns.Add(new DataGridViewImageColumn { Name = "Chart", HeaderText = "流量占比 (Ratio)", FillWeight = 180, SortMode = DataGridViewColumnSortMode.Automatic });

            // 绘图事件
            _grid.CellPainting += Grid_CellPainting;

            // ★★★ 核心修复：自定义排序逻辑 ★★★
            _grid.SortCompare += Grid_SortCompare;

            this.Controls.Add(_grid);
            _grid.BringToFront();
        }

        // ★★★ 新增：自定义排序方法 ★★★
        private void Grid_SortCompare(object sender, DataGridViewSortCompareEventArgs e)
        {
            // 1. 获取两行的 Tag 数据 (long数组)
            // Tag: [0]=Total, [1]=Max, [2]=Up, [3]=Down
            var tag1 = _grid.Rows[e.RowIndex1].Tag as long[];
            var tag2 = _grid.Rows[e.RowIndex2].Tag as long[];

            if (tag1 == null || tag2 == null)
            {
                e.Handled = false;
                return;
            }

            long val1 = 0, val2 = 0;

            // 2. 根据点击的列名，决定比较哪个数值
            switch (e.Column.Name)
            {
                case "Up":
                    val1 = tag1[2]; val2 = tag2[2];
                    break;
                case "Down":
                    val1 = tag1[3]; val2 = tag2[3];
                    break;
                case "Total":
                case "Chart": // 点击图表列也按总流量排序
                    val1 = tag1[0]; val2 = tag2[0];
                    break;
                default:
                    // 对于 "Date" 列，直接使用默认的字符串排序即可 (yyyy-MM-dd 本身就是可排序的)
                    // 如果您希望周报/季报也完美排序，建议保持默认字符序，通常也是对的
                    e.Handled = false;
                    return;
            }

            // 3. 执行数字比较
            e.SortResult = val1.CompareTo(val2);
            e.Handled = true; // 告诉系统：我排完了，你别管了
        }

        private void CreateDirectDash(Panel p, int startX, string title, out Label lblUp, out Label lblDown)
        {
            p.Controls.Add(new Label { Text = title, ForeColor = C_TextDim, Location = new Point(startX, S(15)), AutoSize = true, Font = new Font("Microsoft YaHei UI", 9) });
            lblUp = new Label { Text = "0 KB", ForeColor = C_Up, Location = new Point(startX, S(45)), AutoSize = true, Font = new Font("Consolas", 20, FontStyle.Bold) };
            p.Controls.Add(lblUp);
            p.Controls.Add(new Label { Text = "↑ 上传 (Upload)", ForeColor = C_Up, Location = new Point(startX + S(2), S(85)), AutoSize = true, Font = new Font("Microsoft YaHei UI", 8) });

            int downX = startX + S(165);
            lblDown = new Label { Text = "0 KB", ForeColor = C_Down, Location = new Point(downX, S(45)), AutoSize = true, Font = new Font("Consolas", 20, FontStyle.Bold) };
            p.Controls.Add(lblDown);
            p.Controls.Add(new Label { Text = "↓ 下载 (Download)", ForeColor = C_Down, Location = new Point(downX + S(2), S(85)), AutoSize = true, Font = new Font("Microsoft YaHei UI", 8) });
        }

        private Button CreateBtn(Panel p, string txt, ref int x)
        {
            var btn = new Button
            {
                Text = txt,
                Location = new Point(x, S(12)),
                Size = new Size(S(100), S(30)),
                FlatStyle = FlatStyle.Flat,
                BackColor = C_Back,
                ForeColor = C_TextDim,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 9)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += (s, e) => SwitchView(_viewButtons.FirstOrDefault(kv => kv.Value == s).Key);
            p.Controls.Add(btn);
            x += S(105);
            return btn;
        }

        private void UpdateRealtimeStats()
        {
            _lblSessionUp.Text = MetricUtils.FormatDataSize(_cfg.SessionUploadBytes);
            _lblSessionDown.Text = MetricUtils.FormatDataSize(_cfg.SessionDownloadBytes);
            var today = TrafficLogger.GetTodayStats();
            _lblTodayUp.Text = MetricUtils.FormatDataSize(today.up);
            _lblTodayDown.Text = MetricUtils.FormatDataSize(today.down);
        }

        private void SwitchView(HistoryViewMode mode)
        {
            _currentMode = mode;
            foreach (var kv in _viewButtons)
            {
                bool active = kv.Key == mode;
                kv.Value.BackColor = active ? C_Accent : C_Back;
                kv.Value.ForeColor = active ? Color.White : C_TextDim;
            }
            LoadGridData(mode);
        }

        private void LoadGridData(HistoryViewMode mode)
        {
            _grid.Rows.Clear();
            var raw = TrafficLogger.Data.History;
            if (raw == null || raw.Count == 0)
            {
                _lblListSummary.Text = "暂无历史数据 (No Data)";
                return;
            }

            var list = AggregateData(raw, mode);
            long maxTotal = list.Any() ? list.Max(x => x.Total) : 1;
            if (maxTotal == 0) maxTotal = 1;

            double avg = list.Average(x => x.Total);
            double threshold = Math.Max(avg * 2.0, 1024.0 * 1024 * 1024);

            long sumUp = 0, sumDown = 0;
            foreach (var item in list)
            {
                int idx = _grid.Rows.Add();
                var row = _grid.Rows[idx];

                bool isOutlier = item.Total > threshold;
                string dateText = item.DateLabel;

                // 保留🔥
                // if (isOutlier) dateText = "🔥 " + dateText; // 根据您之前的截图，这里似乎去掉了火，只留颜色？
                // 如果您想保留火，请取消上面的注释。
                // 现在的代码只做高亮颜色处理。

                row.Cells["Date"].Value = dateText;
                row.Cells["Up"].Value = MetricUtils.FormatDataSize(item.Upload);
                row.Cells["Down"].Value = MetricUtils.FormatDataSize(item.Download);



                // ★★★ 修复：将🔥加回 Total 列 ★★★
                string totalText = MetricUtils.FormatDataSize(item.Total);  
                if (isOutlier) totalText = "🔥 " + totalText;
                row.Cells["Total"].Value = totalText;

                if (isOutlier)
                {
                    row.DefaultCellStyle.ForeColor = C_Highlight;
                    row.Cells["Up"].Style.ForeColor = C_Up;
                    row.Cells["Down"].Style.ForeColor = C_Down;
                }

                // 存入数值供排序和绘图使用
                row.Tag = new long[] { item.Total, maxTotal, item.Upload, item.Download };

                sumUp += item.Upload;
                sumDown += item.Download;
            }

            _grid.ClearSelection();
            string modeName = mode switch { HistoryViewMode.Daily => "日", HistoryViewMode.Weekly => "周", HistoryViewMode.Monthly => "月", HistoryViewMode.Quarterly => "季", _ => "年" };
            _lblListSummary.Text = $"{modeName}视图总计 (View Total):  ↑ {MetricUtils.FormatDataSize(sumUp)}    ↓ {MetricUtils.FormatDataSize(sumDown)}    (Σ {MetricUtils.FormatDataSize(sumUp + sumDown)})";
        }

        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex == 4 && e.RowIndex >= 0)
            {
                e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
                var row = _grid.Rows[e.RowIndex];
                if (row.Tag is long[] stats)
                {
                    long total = stats[0], max = stats[1], up = stats[2];
                    if (max > 0)
                    {
                        Rectangle r = e.CellBounds;
                        r.Inflate(-10, -10);
                        using (var brushBg = new SolidBrush(C_BarBg)) e.Graphics.FillRectangle(brushBg, r);

                        double sqrtTotal = Math.Sqrt(total);
                        double sqrtMax = Math.Sqrt(max);
                        float pctTotal = (float)(sqrtTotal / sqrtMax);

                        int wTotal = (int)(r.Width * pctTotal);
                        if (wTotal < 4 && pctTotal > 0) wTotal = 4;

                        if (wTotal > 0)
                        {
                            float pctUp = total > 0 ? (float)up / total : 0;
                            int wUp = (int)(wTotal * pctUp);
                            int wDown = wTotal - wUp;
                            if (wUp > 0) e.Graphics.FillRectangle(new SolidBrush(C_Up), new Rectangle(r.X, r.Y, wUp, r.Height));
                            if (wDown > 0) e.Graphics.FillRectangle(new SolidBrush(C_Down), new Rectangle(r.X + wUp, r.Y, wDown, r.Height));
                        }
                    }
                }
                e.Handled = true;
            }
        }

        private List<DisplayItem> AggregateData(Dictionary<string, DailyRecord> raw, HistoryViewMode mode)
        {
            var source = raw.Select(x => { DateTime.TryParse(x.Key, out var d); return new { D = d, V = x.Value }; }).Where(x => x.D != DateTime.MinValue).ToList();
            return source.GroupBy(x => GetKey(x.D, mode))
                .Select(g => new DisplayItem
                {
                    DateLabel = g.Key,
                    SortDate = g.Max(x => x.D),
                    Upload = g.Sum(x => x.V.Upload),
                    Download = g.Sum(x => x.V.Download)
                })
                .OrderByDescending(x => x.SortDate).ToList();
        }

        private string GetKey(DateTime d, HistoryViewMode m) => m switch
        {
            HistoryViewMode.Daily => d.ToString("yyyy-MM-dd"),
            HistoryViewMode.Weekly => $"{d.Year}-W{CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(d, CalendarWeekRule.FirstDay, DayOfWeek.Monday):00}",
            HistoryViewMode.Monthly => d.ToString("yyyy-MM"),
            HistoryViewMode.Quarterly => $"{d.Year}-Q{(d.Month - 1) / 3 + 1}",
            HistoryViewMode.Yearly => d.ToString("yyyy"),
            _ => d.ToString("yyyy-MM-dd")
        };

        private class DisplayItem { public string DateLabel; public DateTime SortDate; public long Upload; public long Download; public long Total => Upload + Download; }

        protected override void OnFormClosed(FormClosedEventArgs e) 
        { 
            _timer.Stop(); 
            _timer.Dispose();
            base.OnFormClosed(e); 
        }

        // 确保窗口在显示时居中
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 强制窗口居中
            this.StartPosition = FormStartPosition.CenterScreen;
            this.CenterToScreen();
        }
    }
}
