using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;
using Application = System.Windows.Application;

namespace AutoCommand.Matrix
{
    /// <summary>
    /// The overlay window: borderless, per-pixel transparent, always on
    /// top, never focus-stealing (WS_EX_NOACTIVATE), invisible to Alt-Tab
    /// (WS_EX_TOOLWINDOW), and optionally mouse-transparent (WS_EX_TRANSPARENT).
    /// It docks left / right / full-screen; only an explicit "full" in the
    /// config ever covers the whole desktop.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MonitorEngine _engine;
        private readonly MatrixRenderer _renderer;
        private MatrixRainConfig _cfg;

        private WindowInteropHelper _hwnd;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;   // click-through
        private const int WS_EX_NOACTIVATE = 0x08000000;    // never steal focus
        private const int WS_EX_TOOLWINDOW = 0x00000080;    // hide from Alt-Tab

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public MainWindow(MatrixRainConfig cfg, MonitorEngine engine)
        {
            InitializeComponent();
            _cfg = cfg;
            _engine = engine;
            _renderer = new MatrixRenderer(RainImage, engine, cfg);

            // Position before the first Show — a borderless transparent
            // window flashing at (0,0) before docking would be jarring.
            ApplyDock();

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this);
                ApplyWindowExStyles();
            };
            Loaded += (s, e) =>
            {
                ApplyDock();
                _renderer.SetSurfaceSize(ActualWidth, ActualHeight);
                _renderer.Start();
            };
            SizeChanged += (s, e) => _renderer.SetSurfaceSize(ActualWidth, ActualHeight);
            DpiChanged += (s, e) => Dispatcher.BeginInvoke(
                (Action)(() => _renderer.SetSurfaceSize(ActualWidth, ActualHeight)));

            // Manual dock: drag the rain wherever it should live.
            MouseLeftButtonDown += (s, e) =>
            {
                if (!_cfg.ClickThrough && _cfg.Dock == "manual") DragMove();
            };
            LocationChanged += SaveManualBoundsIfManual;
            MouseMove += UpdateHoverTooltip;

            // Screens rearranged (dock/un dock, resolution change) — re-dock.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            // Closing (Alt-F4 etc.) hides to tray; the tray's Exit is the
            // only way out, so a stray keystroke never kills the monitor.
            Closing += (s, e) =>
            {
                if (Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke((Action)ApplyDock);
        }

        // ------------------------------------------------------------------
        // Configuration / hot reload
        // ------------------------------------------------------------------

        /// <summary>Applies a (re)loaded config: window chrome bits, dock
        /// placement, and the renderer's fonts/colors/rates.</summary>
        public void ApplyNewConfig(MatrixRainConfig cfg)
        {
            string oldDock = _cfg.Dock;
            int oldWidth = _cfg.WidthPx;
            _cfg = cfg;

            Opacity = cfg.Opacity;
            ApplyWindowExStyles();
            _renderer.ApplyConfig(cfg);

            if (cfg.Dock != oldDock || cfg.WidthPx != oldWidth || cfg.Dock != "manual")
                ApplyDock();
            _renderer.SetSurfaceSize(ActualWidth, ActualHeight);
        }

        /// <summary>Moves/resizes the window to its dock. Full-desktop is an
        /// explicit choice — the default side docks never exceed the work
        /// area, so the taskbar stays uncovered.</summary>
        public void ApplyDock()
        {
            switch (_cfg.Dock)
            {
                case "left":
                    Left = SystemParameters.WorkArea.Left;
                    Top = SystemParameters.WorkArea.Top;
                    Width = _cfg.WidthPx;
                    Height = SystemParameters.WorkArea.Height;
                    break;
                case "full":
                    Left = 0;
                    Top = 0;
                    Width = SystemParameters.PrimaryScreenWidth;
                    Height = SystemParameters.PrimaryScreenHeight;
                    break;
                case "manual":
                    var b = LoadManualBounds();
                    if (b != null)
                    {
                        Left = b.Left; Top = b.Top;
                        Width = b.Width; Height = b.Height;
                    }
                    else goto default;
                    break;
                default: // right
                    _cfg.Dock = "right";
                    Left = SystemParameters.WorkArea.Right - _cfg.WidthPx;
                    Top = SystemParameters.WorkArea.Top;
                    Width = _cfg.WidthPx;
                    Height = SystemParameters.WorkArea.Height;
                    break;
            }
            _renderer?.SetSurfaceSize(ActualWidth, ActualHeight);
        }

        private void ApplyWindowExStyles()
        {
            if (_hwnd == null || _hwnd.Handle == IntPtr.Zero) return;
            int style = GetWindowLong(_hwnd.Handle, GWL_EXSTYLE);
            style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            if (_cfg.ClickThrough) style |= WS_EX_TRANSPARENT;
            else style &= ~WS_EX_TRANSPARENT;
            SetWindowLong(_hwnd.Handle, GWL_EXSTYLE, style);
        }

        // ------------------------------------------------------------------
        // Hover: the story behind a column
        // ------------------------------------------------------------------

        private void UpdateHoverTooltip(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_cfg.ClickThrough) { ToolTip = null; return; }
            var pos = e.GetPosition(RainImage);
            string story = _renderer.DescribeColumnAt(pos.X);
            if (story == null) { ToolTip = null; return; }
            // ToolTips need an active window on some shell versions; this one
            // is NOACTIVATE, so show a plain one via the ToolTipService path.
            if (ToolTip is TextBlock tb) tb.Text = story;
            else
            {
                ToolTip = new System.Windows.Controls.ToolTip
                {
                    Content = new TextBlock { Text = story, FontFamily = new FontFamily("Consolas") }
                };
            }
        }

        // ------------------------------------------------------------------
        // Manual-dock persistence (matrix_rain_window.json beside the exe)
        // ------------------------------------------------------------------

        private static string ManualBoundsPath =>
            Path.Combine(AppContext.BaseDirectory, "matrix_rain_window.json");

        private record ManualBounds(double Left, double Top, double Width, double Height);

        private DateTime _lastManualSaveUtc = DateTime.MinValue;

        private void SaveManualBoundsIfManual(object sender, EventArgs e)
        {
            if (_cfg.Dock != "manual" || Left < -30000 || double.IsNaN(Left)) return;
            if ((DateTime.UtcNow - _lastManualSaveUtc).TotalMilliseconds < 400) return;
            _lastManualSaveUtc = DateTime.UtcNow;
            try
            {
                File.WriteAllText(ManualBoundsPath, JsonSerializer.Serialize(
                    new ManualBounds(Left, Top, Width, Height),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort persistence only */ }
        }

        private ManualBounds LoadManualBounds()
        {
            try
            {
                if (File.Exists(ManualBoundsPath))
                    return JsonSerializer.Deserialize<ManualBounds>(File.ReadAllText(ManualBoundsPath));
            }
            catch { }
            return null;
        }

        protected override void OnClosed(EventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _renderer.Dispose();
            base.OnClosed(e);
        }
    }
}
