using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public interface ISettingsPage
    {
        void Save();
        void OnShow();
    }

    public class SettingsPageBase : UserControl, ISettingsPage
    {
        protected Settings Config;
        protected MainForm MainForm;
        protected UIController UI;
        
        // ★★★ Refresh Mechanism for Deferred Load ★★★
        protected List<Action> _refreshActions = new List<Action>();

        public static readonly Color GlobalBackColor = Color.FromArgb(249, 249, 249); 

        public SettingsPageBase() 
        {
            this.BackColor = GlobalBackColor; 
            this.Dock = DockStyle.Fill;
            this.DoubleBuffered = true;
        }

        public void SetContext(Settings cfg, MainForm form, UIController ui)
        {
            Config = cfg;
            MainForm = form;
            UI = ui;

            // ★★★ Fix: Ensure UI controls reflect the Config values immediately ★★★
            // This prevents controls from defaulting to false/0 and overwriting the config on Save()
            if (Config != null)
            {
                foreach (var action in _refreshActions)
                {
                    action.Invoke();
                }
            }
        }

        public void RegisterRefresh(Action action)
        {
            _refreshActions.Add(action);
        }

        public virtual void OnShow()
        {
            // Base implementation can be empty or used for common logic
            
            // Execute refresh actions to ensure UI reflects the latest Config
            if (Config != null)
            {
                foreach (var action in _refreshActions)
                {
                    action.Invoke();
                }
            }
        }

        public virtual void Save()
        {
            // Immediate binding means we don't need to do anything here.
            // But we keep the method because ISettingsPage requires it.
            // Subclasses (like PluginPage) can override it for post-save logic.
        }

        protected void ClearAndDispose(Control.ControlCollection controls)
        {
            // Clear pending actions whenever we destroy the UI controls
            _refreshActions.Clear();

            while (controls.Count > 0)
            {
                var c = controls[0];
                controls.RemoveAt(0);
                c.Dispose();
            }
        }

        protected void EnsureSafeVisibility(LiteCheck chkHideMain, LiteCheck chkHideTray, LiteCheck chkShowTaskbar)
        {
            bool hideMain = chkHideMain != null ? chkHideMain.Checked : Config.HideMainForm;
            bool hideTray = chkHideTray != null ? chkHideTray.Checked : Config.HideTrayIcon;
            bool showBar  = chkShowTaskbar != null ? chkShowTaskbar.Checked : Config.ShowTaskbar;

            if (hideMain && hideTray && !showBar)
            {
                MessageBox.Show("为了防止程序无法唤出，不能同时隐藏 [主界面]、[托盘图标] 和 [任务栏]。", 
                                "安全警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                
                if (chkHideMain != null) chkHideMain.Checked = false;
                if (chkHideTray != null) chkHideTray.Checked = false;
                if (chkShowTaskbar != null) chkShowTaskbar.Checked = true;
            }
        }
    }
}
