using LiteMonitor.src.Core;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Net.Http;
using System.Threading.Tasks;

namespace LiteMonitor
{
    public class AboutForm : Form
    {
        public AboutForm()
        {
            // === 1. 语言判断与文案准备 ===
            bool isZh = LanguageManager.CurrentLang == "zh";

            string strTitle = isZh ? "关于 LiteMonitor-Fork" : "About LiteMonitor-Fork";
            string strDesc = isZh
                ? "一款开源软件 LiteMonitor 同人修复版。\n© 2025 Diorser / LiteMonitor Project"
                : "A community fork of LiteMonitor.\n© 2025 Diorser / LiteMonitor Project";
            string strUpdate = LanguageManager.T("Menu.CheckUpdate");
            string strClose = LanguageManager.T("Menu.OK");
            string strBug = LanguageManager.T("Menu.Feedback");

            // === 基础外观 ===
            Text = strTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;

            // ★ DPI 修复（窗口大小保持 360×280 不变）
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;
            ClientSize = UIUtils.S(new Size(360, 280));

            var theme = ThemeManager.Current;
            BackColor = ThemeManager.ParseColor(theme.Color.Background);

            // === 标题 ===
            var lblTitle = new Label
            {
                Text = "⚡️LiteMonitor-Fork",
                Font = new Font(theme.Font.Family, 14, FontStyle.Bold),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextTitle),
                AutoSize = true,
                Location = new Point(UIUtils.S(22), UIUtils.S(28))
            };

            // === 版本号 ===
            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "1.0.0";

            int plus = version.IndexOf('+');
            if (plus > 0) version = version[..plus];

            var lblVer = new Label
            {
                Text = $"Version {version}",
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Location = new Point(UIUtils.S(32), UIUtils.S(68)),
                AutoSize = true
            };

            // === 简介 ===
            var lblDesc = new Label
            {
                Text = strDesc,
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Location = new Point(UIUtils.S(32), UIUtils.S(98)),
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = UIUtils.S(new Size(300, 0))
            };

            // === GitHub 链接 ===
            var githubLink = new LinkLabel
            {
                Text = "GitHub: github.com/YuChen-OmO/LiteMonitor-Fork",
                LinkColor = Color.SkyBlue,
                ActiveLinkColor = Color.LightSkyBlue,
                VisitedLinkColor = Color.DeepSkyBlue,
                Location = new Point(UIUtils.S(32), UIUtils.S(150)),
                AutoSize = true,
                UseMnemonic = false
            };
            githubLink.LinkClicked += (_, __) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/YuChen-OmO/LiteMonitor-Fork")
                    { UseShellExecute = true });
                }
                catch { }
            };

            // === BUG 反馈按钮 ===
            var btnBug = new Button
            {
                Text = strBug,
                Size = UIUtils.S(new Size(100, 30)),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(UIUtils.S(32), UIUtils.S(235)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(theme.Color.GroupBackground),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Font = new Font(theme.Font.Family, 9.5f, FontStyle.Regular)
            };
            btnBug.FlatAppearance.BorderSize = 0;
            btnBug.FlatAppearance.MouseOverBackColor = ThemeManager.ParseColor(theme.Color.BarBackground);
            btnBug.Click += (_, __) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/YuChen-OmO/LiteMonitor-Fork/issues")
                    { UseShellExecute = true });
                }
                catch { }
            };

            // === 检查更新按钮 ===
            var btnCheckUpdate = new Button
            {
                Text = strUpdate,
                Size = UIUtils.S(new Size(100, 30)),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(UIUtils.S(150), UIUtils.S(235)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(theme.Color.GroupBackground),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Font = new Font(theme.Font.Family, 9.5f, FontStyle.Regular)
            };
            btnCheckUpdate.FlatAppearance.BorderSize = 0;
            btnCheckUpdate.FlatAppearance.MouseOverBackColor = ThemeManager.ParseColor(theme.Color.BarBackground);
            btnCheckUpdate.Click += async (_, __) => await UpdateChecker.CheckAsync(showMessage: true);

            // === 关闭按钮 ===
            var btnClose = new Button
            {
                Text = strClose,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Size = UIUtils.S(new Size(70, 30)),
                Location = new Point(UIUtils.S(270), UIUtils.S(235)),
                FlatStyle = FlatStyle.Flat,
                BackColor = ThemeManager.ParseColor(theme.Color.GroupBackground),
                ForeColor = ThemeManager.ParseColor(theme.Color.TextPrimary),
                Font = new Font(theme.Font.Family, 9.5f, FontStyle.Regular)
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = ThemeManager.ParseColor(theme.Color.BarBackground);

            Controls.AddRange([lblTitle, lblVer, lblDesc, githubLink, btnBug, btnCheckUpdate, btnClose]);
        }
    }
}