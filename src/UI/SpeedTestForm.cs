using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.Core;
using System.Diagnostics;
using System.Collections.Generic; // 用于 MakeMovable
using System.Runtime.InteropServices; // 用于 DPI 适配

namespace LiteMonitor
{
    // 自定义进度条控件，支持自定义颜色
    public class CustomProgressBar : ProgressBar
    {
        public CustomProgressBar()
        {
            this.SetStyle(ControlStyles.UserPaint, true);
            this.SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle rect = this.ClientRectangle;
            Graphics g = e.Graphics;

            // 绘制背景
            using (SolidBrush backgroundBrush = new SolidBrush(this.BackColor))
            {
                g.FillRectangle(backgroundBrush, rect);
            }

            // 绘制进度条
            if (this.Value > 0)
            {
                Rectangle progressRect = new Rectangle(
                    rect.X, rect.Y, 
                    (int)(rect.Width * ((double)this.Value / this.Maximum)), 
                    rect.Height
                );

                using (SolidBrush progressBrush = new SolidBrush(this.ForeColor))
                {
                    g.FillRectangle(progressBrush, progressRect);
                }

                // 添加边框
                using (Pen borderPen = new Pen(Color.FromArgb(100, this.ForeColor), 1))
                {
                    g.DrawRectangle(borderPen, progressRect);
                }
            }

            // 绘制边框
            using (Pen borderPen = new Pen(Color.FromArgb(80, this.ForeColor), 1))
            {
                g.DrawRectangle(borderPen, rect);
            }
        }
    }

    public class SpeedTestForm : Form
    {
        // 移除 lblInstantSpeed 的定义和注释
        private Label lblStatus;
        private Label lblSpeed;
        private Label lblLocalSpeed;
        private ProgressBar bar;
        private Button btnClose;
        private Button btnRetry;

        // 测速状态枚举 (已移除 Connection)
        private enum SpeedTestPhase { Idle, Download, Upload, Complete }
        private SpeedTestPhase _currentPhase = SpeedTestPhase.Idle;

        // 测速配置
        private readonly int _downloadSeconds = 15;
        private readonly int _uploadSeconds = 7;

        // 测速结果
        private double lastDownload = 0;
        private double lastUpload = 0;

        // 本地网卡数据
        private double maxLocalDownload = 0;
        private double maxLocalUpload = 0;

        // 窗口拖动相关变量 (仅保留一个)
        private Point _dragOffset;

        // 定时器用于实时更新本地网卡数据
        private System.Windows.Forms.Timer _localDataTimer;

        // 进度条的进度分配
        private const int DownloadStartProgress = 0;
        private const int DownloadEndProgress = 70;
        private const int UploadEndProgress = 95;
        private const int FinalProgress = 100;

        // 按钮宽度和间距
        private const int ButtonWidth = 80;
        private const int ButtonHeight = 30;
        private const int ButtonSpacing = 10;

        // 主题管理器
        private Theme _currentTheme;
        private readonly Settings _cfg;

        // DPI 缩放函数
        private int ScaleDPI(int value)
        {
            using (Graphics g = this.CreateGraphics())
            {
                float dpiScale = g.DpiX / 96f; // 96 DPI 是标准缩放
                return (int)(value * dpiScale);
            }
        }

        public SpeedTestForm()
        {
            // 获取当前主题
            _currentTheme = ThemeManager.Current;
            _cfg = Settings.Load();
            // UI 初始化 (使用主题色)
            FormBorderStyle = FormBorderStyle.None;
            Width = ScaleDPI(400); // 增加宽度以容纳更大的字体和更好的布局
            Height = ScaleDPI(280); // 增加高度以改善布局比例
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ThemeManager.ParseColor(_currentTheme.Color.Background);
            ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextPrimary);
            TopMost = true;
            
            // 设置窗口不在任务栏显示
            ShowInTaskbar = false;
            
            // 设置窗口透明度为主题透明度
            this.Opacity = _cfg.Opacity; // 假设Opacity是0-100的整数
            this.SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.DoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); // 启用透明背景支持和双缓冲

            int currentTop = ScaleDPI(25); // 增加顶部间距

            // 1. 状态标签 (Status) - 使用主题字体和颜色
            lblStatus = new Label
            {
                Text = "🌐 Network Speed Test",
                AutoSize = false,
                Width = Width,
                Height = ScaleDPI(30), // 增加高度以容纳更大的字体
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(_currentTheme.Font.Family, 13, FontStyle.Bold), // 增大字体
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextTitle),
                Top = currentTop
            };
            currentTop += lblStatus.Height + ScaleDPI(12); // 增加间距

            // 2. 本地网卡实时/峰值数据显示 (MB/s) - 大幅增大数值字体，缩小单位
            lblLocalSpeed = new Label
            {
                Text = "Waiting...",
                AutoSize = false,
                Width = Width,
                Height = ScaleDPI(70), // 进一步增加高度以容纳更大的字体
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(_currentTheme.Font.Family, 20, FontStyle.Bold), // 进一步增大数值字体
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.ValueSafe),
                Top = currentTop
            };
            currentTop += lblLocalSpeed.Height + ScaleDPI(15); // 增加间距

            // 3. 服务器测速数据显示 (Mbps) - 使用较小字体显示单位
            lblSpeed = new Label
            {
                Text = "Internet: ↓ 0.0 Mbps ↑ 0.0 Mbps",
                AutoSize = false,
                Width = Width,
                Height = ScaleDPI(22), // 适当增加高度
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(_currentTheme.Font.Family, 10, FontStyle.Bold), // 减小字体大小
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextGroup),
                Top = currentTop
            };
            currentTop += lblSpeed.Height + ScaleDPI(18); // 增加间距

            // 4. 自定义进度条 - 使用主题色
        bar = new CustomProgressBar
        {
            Width = Width - ScaleDPI(80), // 增加边距
            Height = ScaleDPI(16), // 增加高度
            Left = ScaleDPI(40),
            Top = currentTop,
            Maximum = 100,
            BackColor = ThemeManager.ParseColor(_currentTheme.Color.BarBackground),
            ForeColor = ThemeManager.ParseColor(_currentTheme.Color.BarLow)
        };
            currentTop += bar.Height + ScaleDPI(30); // 增加间距

            // 按钮布局调整 (定位在底部)
            int totalButtonWidth = (ScaleDPI(ButtonWidth) * 2) + ScaleDPI(ButtonSpacing);
            int startX = (Width - totalButtonWidth) / 2;
            int buttonY = Height - ScaleDPI(ButtonHeight) - ScaleDPI(30); // 增加底部间距

            // 1. 关闭/退出按钮 - 使用主题色
            btnClose = new Button
            {
                Text = "Exit",
                Width = ScaleDPI(ButtonWidth),
                Height = ScaleDPI(ButtonHeight),
                Top = buttonY,
                Left = (Width - ScaleDPI(ButtonWidth)) / 2, // 修改：居中显示
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(_currentTheme.Color.GroupBackground),
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextPrimary),
                Visible = true // 修改：默认显示
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += (_, __) => this.Close();

            // 2. 重试按钮 (使用主题强调色)
            btnRetry = new Button
            {
                Text = "Retry",
                Width = ScaleDPI(ButtonWidth),
                Height = ScaleDPI(ButtonHeight),
                Top = buttonY,
                Left = startX, // 保持原位置
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(_currentTheme.Color.GroupBackground),
                ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextPrimary),
                Visible = false // 保持默认隐藏
            };
            btnRetry.FlatAppearance.BorderSize = 0;
            btnRetry.Click += BtnRetry_Click;

            // 初始化本地网卡数据定时器 
            _localDataTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _localDataTimer.Tick += UpdateLocalNetworkData;

            // 添加控件
            Controls.Add(lblStatus);
            Controls.Add(lblLocalSpeed);
            Controls.Add(lblSpeed);
            Controls.Add(bar);
            Controls.Add(btnClose);
            Controls.Add(btnRetry);

            // 核心优化：移除冗余的拖拽代码，使用 MakeMovable 方法
            MakeMovable(this);
            foreach (Control control in Controls)
            {
                MakeMovable(control);
            }

            ApplyRounded();
        }

        // 核心优化：抽象拖拽逻辑，减少代码冗余
        private void MakeMovable(Control control)
        {
            control.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) _dragOffset = e.Location;
            };
            control.MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.X - _dragOffset.X) + Math.Abs(e.Y - _dragOffset.Y) < 1) return;
                    Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
                }
            };
        }

        // 重试点击事件处理
        private void BtnRetry_Click(object sender, EventArgs e)
        {
            // 批量重置 UI 状态
            Invoke(new Action(() =>
            {
                lblStatus.Text = "🚀 Speed Test Started...";
                lblSpeed.Text = "Internet: ↓ 0.0 Mbps ↑ 0.0 Mbps";
                lblLocalSpeed.Text = "Measuring network traffic...";
                lblLocalSpeed.ForeColor = ThemeManager.ParseColor(_currentTheme.Color.ValueSafe);
                bar.Value = 0;
                btnClose.Visible = true; // 修改：恢复显示关闭按钮
                btnClose.Left = (Width - ScaleDPI(ButtonWidth)) / 2; // 修改：恢复居中位置
                btnRetry.Visible = false;
            }));

            // 重置峰值
            maxLocalDownload = 0;
            maxLocalUpload = 0;
            _currentPhase = SpeedTestPhase.Idle;

            // 重新开始测试
            Task.Run(RunTest);
        }

        // 网卡数据更新逻辑 (保持不变)
        private void UpdateLocalNetworkData(object sender, EventArgs e)
        {
            try
            {
                var hardwareMonitor = HardwareMonitor.Instance;
                if (hardwareMonitor != null)
                {
                    float? uploadBps = hardwareMonitor.Get("NET.Up");
                    float? downloadBps = hardwareMonitor.Get("NET.Down");

                    // 转换为MB/s
                    double currentLocalDownload = downloadBps.HasValue ? downloadBps.Value / 1024f / 1024f : 0f;
                    double currentLocalUpload = uploadBps.HasValue ? uploadBps.Value / 1024f / 1024f : 0f;

                    if (_currentPhase == SpeedTestPhase.Download)
                    {
                        if (currentLocalDownload > maxLocalDownload) maxLocalDownload = currentLocalDownload;
                    }

                    if (_currentPhase == SpeedTestPhase.Upload)
                    {
                        if (currentLocalUpload > maxLocalUpload) maxLocalUpload = currentLocalUpload;
                    }

                    // 实时更新 UI 
                    Invoke(new Action(() =>
                    {
                        if (_currentPhase == SpeedTestPhase.Download)
                        {
                            lblLocalSpeed.Text = $" ↓ {currentLocalDownload:F1}MB/s   ↑ 0.0MB/s";
                        }
                        else if (_currentPhase == SpeedTestPhase.Upload)
                        {
                            lblLocalSpeed.Text = $" ↓ {maxLocalDownload:F1}MB/s   ↑ {currentLocalUpload:F1}MB/s";
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeedTestForm] Local network data update failed: {ex.Message}");
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 强制居中 (兼容不同DPI和屏幕配置)
            if (Owner != null)
                CenterToParent();
            else
            {
                Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(
                    screen.Left + (screen.Width - Width) / 2,
                    screen.Top + (screen.Height - Height) / 2
                );
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _localDataTimer.Start();
            Task.Run(RunTest);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _localDataTimer?.Stop();
            _localDataTimer?.Dispose();
            // 标记为已释放，防止异步任务继续回调
            _isDisposed = true;
        }

        // 添加标志位
        private bool _isDisposed = false;

        // ApplyRounded (使用主题圆角)
        private void ApplyRounded()
        {
            try
            {
                var gp = new System.Drawing.Drawing2D.GraphicsPath();
                int cornerRadius = Math.Max(ScaleDPI(4), _currentTheme.Layout.CornerRadius); // 使用主题圆角，最小4px
                int diameter = cornerRadius * 2;
                gp.AddArc(0, 0, diameter, diameter, 180, 90);
                gp.AddArc(Width - diameter, 0, diameter, diameter, 270, 90);
                gp.AddArc(Width - diameter, Height - diameter, diameter, diameter, 0, 90);
                gp.AddArc(0, Height - diameter, diameter, diameter, 90, 90);
                gp.CloseFigure();
                Region = new Region(gp);
            }
            catch
            {
                // 如果圆角设置失败，使用默认圆角
                var gp = new System.Drawing.Drawing2D.GraphicsPath();
                gp.AddArc(0, 0, ScaleDPI(20), ScaleDPI(20), 180, 90);
                gp.AddArc(Width - ScaleDPI(20), 0, ScaleDPI(20), ScaleDPI(20), 270, 90);
                gp.AddArc(Width - ScaleDPI(20), Height - ScaleDPI(20), ScaleDPI(20), ScaleDPI(20), 0, 90);
                gp.AddArc(0, Height - ScaleDPI(20), ScaleDPI(20), ScaleDPI(20), 90, 90);
                gp.CloseFigure();
                Region = new Region(gp);
            }
        }

        private async Task RunTest()
        {
            // 核心优化：启动时一次性设置所有 UI 状态
            Invoke(new Action(() =>
            {
                // 移除 btnClose.Visible = false; 这行，让关闭按钮保持默认显示状态
                btnRetry.Visible = false;
                lblStatus.Text = "🚀 Speed Test Started...";
                lblSpeed.Text = "Internet: ↓ 0.0 Mbps ↑ 0.0 Mbps";
                bar.Value = DownloadStartProgress;
                lblLocalSpeed.Text = "Connecting to server...";
                lblLocalSpeed.ForeColor = ThemeManager.ParseColor(_currentTheme.Color.ValueSafe);
            }));

            // ----------------------------------------------------
            // 1. 下载测速 (Download) - 进度 0% - 70%
            // ----------------------------------------------------
            _currentPhase = SpeedTestPhase.Download;
            Invoke(new Action(() =>
            {
                lblStatus.Text = "▶ Download Test Starting...";
                lblSpeed.Text = "Connecting to server...";
            }));

            lastDownload = await RunDownloadPhase(DownloadStartProgress, DownloadEndProgress);

            // ----------------------------------------------------
            // 2. 锁定下载结果：批量更新，消除闪烁
            // ----------------------------------------------------
            Invoke(new Action(() =>
            {
                lblStatus.Text = "✅ Download Test Complete";
                lblSpeed.Text = $"Internet: ↓ {lastDownload:F1} Mbps   ↑ 0.0 Mbps";
                lblLocalSpeed.Text = $" ↓ {maxLocalDownload:F1}MB/s   ↑ 0.0MB/s";
                lblLocalSpeed.ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextPrimary);
            }));

            // ----------------------------------------------------
            // 3. 上传测速 (Upload) - 进度 70% - 95%
            // ----------------------------------------------------
            _currentPhase = SpeedTestPhase.Upload;
            Invoke(new Action(() =>
            {
                lblStatus.Text = "▶ Upload Test Starting...";
                lblSpeed.Text = $"Internet: ↓ {lastDownload:F1} Mbps   ↑ Connecting...";
                lblLocalSpeed.ForeColor = ThemeManager.ParseColor(_currentTheme.Color.ValueSafe);
            }));

            lastUpload = await RunUploadPhase(DownloadEndProgress, UploadEndProgress);

            // ----------------------------------------------------
            // 4. 锁定上传结果：批量更新，消除闪烁
            // ----------------------------------------------------
            Invoke(new Action(() =>
            {
                lblStatus.Text = "✅ Upload Test Complete";
                lblSpeed.Text = $"Internet: ↓ {lastDownload:F1} Mbps   ↑ {lastUpload:F1} Mbps";
            }));

            // ----------------------------------------------------
            // 5. 最终报告 (Final Report) - 进度 95% - 100%
            // ----------------------------------------------------
            _currentPhase = SpeedTestPhase.Complete;
            Invoke(new Action(() =>
            {
                lblStatus.Text = "🎯 Speed Test Complete";

                // 锁定 lblLocalSpeed 为最终峰值
                lblLocalSpeed.Text = $" ↓ {maxLocalDownload:F1}MB/s   ↑ {maxLocalUpload:F1}MB/s";
                lblLocalSpeed.ForeColor = ThemeManager.ParseColor(_currentTheme.Color.TextPrimary);

                // 统一显示服务器和本地结果
                lblSpeed.Text = $"Internet: ↓ {lastDownload:F1} Mbps   ↑ {lastUpload:F1} Mbps";

                bar.Value = FinalProgress;

                // 测速完成后显示退出和重试按钮
                btnClose.Text = "Exit";
                int totalButtonWidth = (ScaleDPI(ButtonWidth) * 2) + ScaleDPI(ButtonSpacing);
                int startX = (Width - totalButtonWidth) / 2;
                btnClose.Left = startX + ScaleDPI(ButtonWidth) + ScaleDPI(ButtonSpacing); // 移动到右侧位置
                btnClose.Visible = true;
                btnRetry.Visible = true;
            }));
        }

        // ===========================================
        // 下载测速（使用 Stopwatch 追踪时间）
        // ===========================================
        private async Task<double> RunDownloadPhase(int startProgress, int endProgress)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int totalDurationMs = _downloadSeconds * 1000;
            
            // ★★★ 优化：UI 刷新限频变量 ★★★
            long lastUiTick = 0;
            int lastSeconds = -1;

            double result = await NetworkSpeedTester.TestDownloadAsync(
                durationSec: _downloadSeconds,
                threads: 8,
                progress: speed => 
                {
                    // ★★★ 核心修复：限频逻辑 ★★★
                    // 1. 在后台线程直接判断时间，只有超过 100ms 才进入 Invoke
                    // 这样可以避免每秒数千次向 UI 线程发送消息，极大降低了界面卡顿和内存压力
                    long now = stopwatch.ElapsedMilliseconds;
                    if (_isDisposed) return; // 检查是否已释放
                    if (now - lastUiTick < 100 && now < totalDurationMs) return;
                    lastUiTick = now;

                    Invoke(new Action(() =>
                    {
                        if (_isDisposed || IsDisposed) return; // 双重检查
                        // 1. 更新速度
                        lblSpeed.Text = $"Internet: ↓ {speed:F1} Mbps   ↑ 0.0 Mbps";

                        // 2. 更新进度条 (基于时间)
                        double timeRatio = (double)now / totalDurationMs;
                        int progressRange = endProgress - startProgress;
                        int progressValue = startProgress + (int)(timeRatio * progressRange);
                        bar.Value = Math.Min(progressValue, endProgress);

                        // 3. 核心优化：简化状态文本 + 防抖
                        int remainingSeconds = (int)Math.Ceiling((totalDurationMs - now) / 1000.0);
                        // ★★★ 优化：只有整数秒变化时才分配新字符串 ★★★
                        if (remainingSeconds != lastSeconds)
                        {
                            lastSeconds = remainingSeconds;
                            lblStatus.Text = $"Downloading... ({remainingSeconds}s)";
                        }
                    }));
                }
            );
            stopwatch.Stop();
            Invoke(new Action(() => bar.Value = endProgress));
            return result;
        }

        // ===========================================
        // 上传测速（使用 Stopwatch 追踪时间）
        // ===========================================
        private async Task<double> RunUploadPhase(int startProgress, int endProgress)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int totalDurationMs = _uploadSeconds * 1000;

            // ★★★ 优化：UI 刷新限频变量 ★★★
            long lastUiTick = 0;
            int lastSeconds = -1;

            double result = await NetworkSpeedTester.TestUploadAsync(
                durationSec: _uploadSeconds,
                threads: 8,
                progress: speed => 
                {
                    // ★★★ 核心修复：限频逻辑 ★★★
                    long now = stopwatch.ElapsedMilliseconds;
                    if (_isDisposed) return; // 检查是否已释放
                    if (now - lastUiTick < 100 && now < totalDurationMs) return;
                    lastUiTick = now;

                    Invoke(new Action(() =>
                    {
                        if (_isDisposed || IsDisposed) return; // 双重检查
                        // 1. 更新速度
                        lblSpeed.Text = $"Internet: ↓ {lastDownload:F1} Mbps   ↑ {speed:F1} Mbps";

                        // 2. 更新进度条 (基于时间)
                        double timeRatio = (double)now / totalDurationMs;
                        int progressRange = endProgress - startProgress;
                        int progressValue = startProgress + (int)(timeRatio * progressRange);
                        bar.Value = Math.Min(progressValue, endProgress);

                        // 3. 核心优化：简化状态文本 + 防抖
                        int remainingSeconds = (int)Math.Ceiling((totalDurationMs - now) / 1000.0);
                        if (remainingSeconds != lastSeconds)
                        {
                            lastSeconds = remainingSeconds;
                            lblStatus.Text = $"Uploading... ({remainingSeconds}s)";
                        }
                    }));
                }
            );
            stopwatch.Stop();
            Invoke(new Action(() => bar.Value = endProgress));
            return result;
        }
    }
}