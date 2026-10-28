using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AutoCommand.Matrix
{
    /// <summary>
    /// The rain has no title bar, so this tray icon IS the UI: dock side,
    /// click-through, labels, editing the config, reload, exit — plus a
    /// balloon channel for engine status (Sysmon missing, sniffer denied…).
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly WinForms.NotifyIcon _notify;
        private readonly Func<MatrixRainConfig> _cfg;
        private readonly MainWindow _window;
        private IntPtr _hIcon = IntPtr.Zero;
        private readonly WinForms.ToolStripMenuItem _dockLeft, _dockRight, _dockFull, _dockManual;
        private readonly WinForms.ToolStripMenuItem _clickThroughItem, _labelsItem, _rxItem, _desktopItem;
        private DateTime _lastBalloonUtc = DateTime.MinValue;

        public TrayIcon(Func<MatrixRainConfig> cfg, MainWindow window)
        {
            _cfg = cfg;
            _window = window;

            _notify = new WinForms.NotifyIcon
            {
                Text = "AutoCommand Matrix",
                Icon = BuildIcon(),
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();

            var header = new WinForms.ToolStripMenuItem("AutoCommand Matrix") { Enabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            _dockLeft = Item("Dock left", () => SetDock("left"));
            _dockRight = Item("Dock right", () => SetDock("right"));
            _dockFull = Item("Dock full screen", () => SetDock("full"));
            _dockManual = Item("Manual (drag the rain)", () => SetDock("manual"));
            menu.Items.Add(_dockLeft);
            menu.Items.Add(_dockRight);
            menu.Items.Add(_dockFull);
            menu.Items.Add(_dockManual);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            _clickThroughItem = Item("Click-through", () => ToggleProp(c => c.ClickThrough = !c.ClickThrough));
            _labelsItem = Item("PID labels", () => ToggleProp(c => c.ShowLabels = !c.ShowLabels));
            _rxItem = Item("Rain on receives too", () => ToggleProp(c => c.RxMode = !c.RxMode));
            _desktopItem = Item("Over the desktop only", () => ToggleProp(c =>
                c.Surface = c.Surface == "desktop" ? "overlay" : "desktop"));
            menu.Items.Add(_desktopItem);
            menu.Items.Add(_clickThroughItem);
            menu.Items.Add(_labelsItem);
            menu.Items.Add(_rxItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            menu.Items.Add(Item("Edit matrix_rain.json", EditConfig));
            menu.Items.Add(Item("Reload config", ReloadConfig));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(Item("Exit", Exit));

            _notify.ContextMenuStrip = menu;
            SyncFrom(_cfg());
        }

        private WinForms.ToolStripMenuItem Item(string text, Action onClick)
        {
            var item = new WinForms.ToolStripMenuItem(text);
            item.Click += (s, e) => Application.Current.Dispatcher.Invoke(onClick);
            return item;
        }

        private void SetDock(string dock)
        {
            var cfg = _cfg();
            cfg.Dock = dock;
            SaveAndApply(cfg);
        }

        private void ToggleProp(Action<MatrixRainConfig> mutate)
        {
            var cfg = _cfg();
            mutate(cfg);
            SaveAndApply(cfg);
        }

        private void SaveAndApply(MatrixRainConfig cfg)
        {
            MatrixRainConfig.Save(cfg);
            _window.ApplyNewConfig(cfg);
            SyncFrom(cfg);
        }

        private void EditConfig()
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", MatrixRainConfig.ConfigPath)
                {
                    UseShellExecute = true
                });
            }
            catch { /* notepad refused — the file path is in the README */ }
        }

        private void ReloadConfig()
        {
            var cfg = MatrixRainConfig.LoadOrDefault();
            _window.ApplyNewConfig(cfg);
            SyncFrom(cfg);
        }

        private void Exit()
        {
            Application.Current.Shutdown();
        }

        /// <summary>Reflects the config into check marks.</summary>
        public void SyncFrom(MatrixRainConfig cfg)
        {
            _dockLeft.Checked = cfg.Dock == "left";
            _dockRight.Checked = cfg.Dock == "right";
            _dockFull.Checked = cfg.Dock == "full";
            _dockManual.Checked = cfg.Dock == "manual";
            _clickThroughItem.Checked = cfg.ClickThrough;
            _labelsItem.Checked = cfg.ShowLabels;
            _rxItem.Checked = cfg.RxMode;
            _desktopItem.Checked = cfg.Surface == "desktop";
        }

        /// <summary>Engine status → balloon, rate-limited so a flapping
        /// engine can't balloon-spam the corner.</summary>
        public void Status(string message)
        {
            if ((DateTime.UtcNow - _lastBalloonUtc).TotalSeconds < 10) return;
            _lastBalloonUtc = DateTime.UtcNow;
            try
            {
                _notify.ShowBalloonTip(4000, "AutoCommand Matrix", message, WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        /// <summary>A little green "M" tile — the holiday ornament.</summary>
        private Drawing.Icon BuildIcon()
        {
            using var bmp = new Drawing.Bitmap(32, 32);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(Drawing.Color.FromArgb(6, 14, 8));
                using var frame = new Drawing.Pen(Drawing.Color.FromArgb(0, 255, 65), 2);
                g.DrawRectangle(frame, 1, 1, 30, 30);
                using var font = new Drawing.Font("Consolas", 17f, Drawing.FontStyle.Bold);
                using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(232, 255, 232));
                g.DrawString("M", font, brush, 7.5f, 0.5f);
            }
            _hIcon = bmp.GetHicon();
            return Drawing.Icon.FromHandle(_hIcon);
        }

        public void Dispose()
        {
            _notify.Visible = false;
            _notify.Dispose();
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }
    }
}
