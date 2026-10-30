using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint uFlags);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int GWL_STYLE = -32;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SMTO_NORMAL = 0x0000;

        private IntPtr _desktopParent = IntPtr.Zero;

        public MainWindow(MatrixRainConfig cfg, MonitorEngine engine)
        {
            InitializeComponent();
            _cfg = cfg;
            _engine = engine;
            _renderer = new MatrixRenderer(AmbientImage, RainImage, () => engine.Latest, cfg);

            // Position before the first Show — a borderless transparent
            // window flashing at (0,0) before docking would be jarring.
            ApplyDock();

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this);
                ApplySurface();
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
        /// placement, surface (desktop vs overlay), and the renderer's
        /// fonts/colors/rates.</summary>
        public void ApplyNewConfig(MatrixRainConfig cfg)
        {
            string oldDock = _cfg.Dock;
            string oldSurface = _cfg.Surface;
            int oldWidth = _cfg.WidthPx;
            _cfg = cfg;

            if (cfg.Surface != oldSurface) ApplySurface();
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
            // On the desktop surface the pane lives under the icon view;
            // staying mouse-transparent keeps desktop clicks and the icon
            // selection marquee working through it.
            if (_cfg.ClickThrough || _cfg.Surface == "desktop") style |= WS_EX_TRANSPARENT;
            else style &= ~WS_EX_TRANSPARENT;
            SetWindowLong(_hwnd.Handle, GWL_EXSTYLE, style);
        }

        // ------------------------------------------------------------------
        // Desktop surface — the rain behind application windows
        // ------------------------------------------------------------------

        /// <summary>desktop = parented to the wallpaper host (WorkerW),
        /// painted over the desktop but COVERED by application windows —
        /// visible only where the desktop shows. overlay = the original
        /// always-on-top pane.</summary>
        public void ApplySurface()
        {
            if (_hwnd == null || _hwnd.Handle == IntPtr.Zero) return;
            IntPtr h = _hwnd.Handle;

            if (_cfg.Surface == "desktop")
            {
                if (_desktopParent == IntPtr.Zero) _desktopParent = FindWallpaperHost();
                if (_desktopParent == IntPtr.Zero)
                {
                    // No wallpaper host (unusual shell) — stay an overlay so
                    // the rain is never lost entirely.
                    Topmost = true;
                    return;
                }
                Topmost = false;
                int style = GetWindowLong(h, GWL_STYLE);
                SetWindowLong(h, GWL_STYLE, (style | WS_CHILD) & ~WS_POPUP);
                SetParent(h, _desktopParent);
                SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
            else
            {
                _desktopParent = IntPtr.Zero;
                SetParent(h, IntPtr.Zero);
                int style = GetWindowLong(h, GWL_STYLE);
                SetWindowLong(h, GWL_STYLE, (style | WS_POPUP) & ~WS_CHILD);
                SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                Topmost = true;
            }
            ApplyDock();
        }

        /// <summary>The wallpaper-engine dance — discovery by enumeration:
        /// locate the top-level window hosting the desktop icon view
        /// (SHELLDLL_DefView), nudge it with 0x052C so the wallpaper
        /// WorkerW exists, and return the first WorkerW BELOW that host in
        /// Z-order. The pane parents to it and the shell's own z-order
        /// keeps icons and app windows above the rain. (FindWindow is
        /// deliberately not used: on newer shells it fails to see the
        /// desktop host by name even though enumeration finds it.)
        /// WorkerW lives at the primary origin, so the child's coordinates
        /// stay screen coordinates.</summary>
        private static IntPtr FindWallpaperHost()
        {
            var tops = TopWindows();
            int hostIdx = FindIconViewHost(tops);
            if (hostIdx < 0) return IntPtr.Zero;

            // Nudge the icon-view host (Progman on every build seen so far)
            // so the wallpaper WorkerW exists — harmless if it already does.
            SendMessageTimeout(tops[hostIdx], 0x052C, IntPtr.Zero, IntPtr.Zero,
                SMTO_NORMAL, 1000, out _);

            // The spawn can change the Z-order — re-enumerate.
            tops = TopWindows();
            hostIdx = FindIconViewHost(tops);
            if (hostIdx < 0) return IntPtr.Zero;

            for (int i = hostIdx + 1; i < tops.Count; i++)
                if (ClassName(tops[i]) == "WorkerW") return tops[i];
            return IntPtr.Zero;
        }

        private static List<IntPtr> TopWindows()
        {
            var tops = new List<IntPtr>(96);
            EnumWindows((h, _) => { tops.Add(h); return true; }, IntPtr.Zero);
            return tops;
        }

        /// <summary>Index of the first top-level window that parents the
        /// desktop icon view; -1 when not found.</summary>
        private static int FindIconViewHost(List<IntPtr> tops)
        {
            for (int i = 0; i < tops.Count; i++)
                if (HasShellDefViewChild(tops[i])) return i;
            return -1;
        }

        private static bool HasShellDefViewChild(IntPtr h)
        {
            bool found = false;
            EnumChildWindows(h, (c, _) =>
            {
                if (ClassName(c) == "SHELLDLL_DefView") { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
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
