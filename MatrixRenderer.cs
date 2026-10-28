using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

namespace AutoCommand.Matrix
{
    /// <summary>One falling stream inside a column: just its head row. The
    /// glyph a head vacates is redrawn as trail and then left to the frame
    /// fade — cells are drawn exactly once and decay on their own.</summary>
    internal sealed class RainDrop
    {
        public int Row;
        public bool Rx;
    }

    /// <summary>A rain column: the per-PID state the snapshots feed.</summary>
    internal sealed class RainColumn
    {
        public int Pid;
        public int Seq;                 // creation order — stable column position
        public string Name = "";
        public bool Blocked, Exited;
        public long LastTx, LastRx;
        public DateTime LastActivityUtc = DateTime.MinValue;
        public DateTime LastHeartbeatUtc = DateTime.MinValue;
        public DateTime MissingSinceUtc = DateTime.MinValue;
        public double Budget;           // letter credits: 1 letter = 1 row step
        public double RxBudget;
        public readonly List<RainDrop> Drops = new(4);
        public int Slot;
        public long TxBytes, RxBytes, TxPackets, RxPackets;
        public int Connections;
    }

    /// <summary>
    /// The rain itself. Every frame: fade the previous frame toward
    /// transparent, clear and redraw the PID label band, then let each
    /// column spend the letter budget its PID's transmits have banked —
    /// every TX packet buys one falling glyph step in that PID's column.
    /// A traffic-blocked PID spends its budget in red.
    /// </summary>
    public sealed class MatrixRenderer : IDisposable
    {
        private readonly Image _image;
        private readonly MonitorEngine _engine;
        private readonly Random _rand = new();

        private MatrixRainConfig _cfg;
        private GlyphAtlas _atlas;
        private WriteableBitmap _bmp;

        private int _w, _h;              // device pixels
        private int _bandH;              // label band height (device px)
        private int _gridCols, _gridRows;

        private readonly Dictionary<int, RainColumn> _columns = new();
        private int _creationSeq;
        private IReadOnlyList<PidSnapshot> _consumed = Array.Empty<PidSnapshot>();

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastFrameSec;
        private bool _running;

        private const int MaxDropsPerColor = 3;
        private const int PidLabelRows = 6;   // PIDs up to 6 digits, vertically

        public MatrixRenderer(Image image, MonitorEngine engine, MatrixRainConfig cfg)
        {
            _image = image;
            _engine = engine;
            ApplyConfig(cfg);
        }

        public GlyphAtlas Atlas => _atlas;

        /// <summary>Hot-applies config: font/color/charset changes rebuild
        /// the atlas; rates and caps take effect on the next frame.</summary>
        public void ApplyConfig(MatrixRainConfig cfg)
        {
            bool rebuild = _cfg == null || cfg.VisualsDifferFrom(_cfg);
            _cfg = cfg;
            if (rebuild || _atlas == null)
                _atlas = GlyphAtlas.Build(cfg, DpiScale);
            RecomputeGrid();
        }

        private double DpiScale
        {
            get
            {
                var src = PresentationSource.FromVisual(_image);
                return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
        }

        /// <summary>(Re)sizes the rain surface to device pixels; called on
        /// load, dock changes and DPI changes.</summary>
        public void SetSurfaceSize(double dipWidth, double dipHeight)
        {
            double scale = DpiScale;

            // The atlas is typically first built before the window has a
            // presentation source (scale reads as 1.0) — rebuild it once the
            // real per-monitor scale is known, so glyph cells match the
            // surface pixel-for-pixel.
            if (_atlas != null && Math.Abs(scale - _atlas.BuiltWithScale) > 0.01)
            {
                _atlas = GlyphAtlas.Build(_cfg, scale);
                RecomputeGrid();
            }

            int w = Math.Max(1, (int)Math.Ceiling(dipWidth * scale));
            int h = Math.Max(1, (int)Math.Ceiling(dipHeight * scale));
            if (w == _w && h == _h && _bmp != null) return;

            _w = w; _h = h;
            _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
            _image.Source = _bmp;
            _image.Stretch = Stretch.Fill;
            RecomputeGrid();
        }

        private void RecomputeGrid()
        {
            if (_atlas == null || _w == 0) return;
            _bandH = _cfg.ShowLabels ? 3 + PidLabelRows * _atlas.LabelCellH + 3 : 0;
            _gridCols = Math.Max(1, _w / _atlas.CellW);
            _gridRows = Math.Max(1, (_h - _bandH) / _atlas.CellH);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _lastFrameSec = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += Frame;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= Frame;
        }

        // ------------------------------------------------------------------
        // Frame
        // ------------------------------------------------------------------

        private void Frame(object sender, EventArgs e)
        {
            if (_bmp == null || _w == 0 || _h == 0) return;

            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastFrameSec;

            // Big surfaces (the full-desktop dock) render at 30 fps — a
            // 2M-pixel fade pass every vsync buys nothing visually.
            int fps = _w * _h > 1_500_000 ? 30 : 60;
            if (dt < 1.0 / fps - 0.0005) return;
            _lastFrameSec = now;
            dt = Math.Clamp(dt, 0.001, 0.25);

            ConsumeSnapshot();

            _bmp.Lock();
            try
            {
                Fade(dt);

                int stride = _bmp.BackBufferStride;
                if (_cfg.ShowLabels)
                    unsafe { new Span<byte>((void*)_bmp.BackBuffer, _bandH * stride).Clear(); }

                int slot = 0;
                foreach (var col in _columns.Values.OrderBy(c => c.Seq))
                {
                    col.Slot = slot++;
                    if (slot >= _gridCols) break;
                    if (_cfg.ShowLabels) DrawPidLabel(col, stride);
                    StepColumn(col, dt);
                }

                _bmp.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
            }
            finally
            {
                _bmp.Unlock();
            }
        }

        /// <summary>A new snapshot from the engine: convert absolute counter
        /// movement into letter budgets, create/retire columns. Snapshots are
        /// immutable and counters absolute, so a skipped one loses nothing.</summary>
        private void ConsumeSnapshot()
        {
            var snap = _engine.Latest;
            if (ReferenceEquals(snap, _consumed)) return;
            _consumed = snap;

            int cap = Math.Min(Math.Max(4, _cfg.MaxColumns), _gridCols);
            var now = DateTime.UtcNow;
            var seen = new HashSet<int>();

            foreach (var p in snap)
            {
                seen.Add(p.Pid);
                if (!_columns.TryGetValue(p.Pid, out var col))
                {
                    // At cap, a newcomer takes over the stalest idle column.
                    if (_columns.Count >= cap)
                    {
                        RainColumn stalest = null;
                        foreach (var c in _columns.Values)
                            if (stalest == null || c.LastActivityUtc < stalest.LastActivityUtc)
                                stalest = c;
                        if (stalest == null || (now - stalest.LastActivityUtc).TotalSeconds < 30)
                            continue; // everyone is busy — the PID gets no column yet
                        _columns.Remove(stalest.Pid);
                    }
                    col = new RainColumn
                    {
                        Pid = p.Pid,
                        Seq = _creationSeq++,
                        LastTx = p.TxPackets,
                        LastRx = p.RxPackets
                    };
                    _columns[p.Pid] = col;
                }

                col.Name = p.Name;
                col.Blocked = p.Blocked;
                col.Exited = p.Exited;
                col.TxBytes = p.TxBytes; col.RxBytes = p.RxBytes;
                col.TxPackets = p.TxPackets; col.RxPackets = p.RxPackets;
                col.Connections = p.Connections;
                col.MissingSinceUtc = DateTime.MinValue;

                long dTx = p.TxPackets - col.LastTx;
                long dRx = p.RxPackets - col.LastRx;
                if (dTx > 0) col.Budget = Math.Min(col.Budget + dTx * _cfg.LettersPerTransmit, 600);
                if (dRx > 0 && _cfg.RxMode) col.RxBudget = Math.Min(col.RxBudget + dRx * _cfg.LettersPerTransmit, 600);
                // A backwards jump means the counter restarted (new row set on
                // PID reuse) — re-anchor instead of flooding letters.

                // While blocked, the sniffer sees nothing (the firewall drops
                // the packet before the wire) — each fresh connection attempt
                // still rolls red.
                if (p.Blocked && p.Attempts > 0)
                    col.Budget = Math.Min(col.Budget + p.Attempts * Math.Max(1.0, _cfg.LettersPerTransmit), 600);

                if (dTx > 0 || p.Attempts > 0) col.LastActivityUtc = now;
                col.LastTx = p.TxPackets;
                col.LastRx = p.RxPackets;
            }

            // Columns whose PID stayed absent — exited, or the row was handed
            // to a new PID — retire softly: residues keep decaying on their own.
            foreach (var pid in _columns.Keys.ToArray())
            {
                var col = _columns[pid];
                if (seen.Contains(pid)) continue;
                if (col.MissingSinceUtc == DateTime.MinValue) col.MissingSinceUtc = now;
                else if ((now - col.MissingSinceUtc).TotalSeconds > 12) _columns.Remove(pid);
            }
        }

        /// <summary>Multiplies every rain pixel (below the label band) toward
        /// transparent — the trails' decay. Premultiplied pixels: scaling all
        /// four channels is a correct linear fade.</summary>
        private unsafe void Fade(double dt)
        {
            int f = (int)Math.Clamp(Math.Pow(0.055, dt) * 256.0, 0, 255);
            int stride = _bmp.BackBufferStride;
            byte* start = (byte*)_bmp.BackBuffer + _bandH * stride;
            long byteCount = (long)(_h - _bandH) * stride;
            if (byteCount <= 0) return;

            byte* end = start + byteCount;
            for (byte* p = start; p < end; p++)
            {
                if (*p != 0) *p = (byte)((*p * f) >> 8);
            }
        }

        /// <summary>The PID reads vertically down its column — a rain column
        /// is one glyph wide, so sideways labels would spill into the
        /// neighbor's rain. Hover (when not click-through) shows the full
        /// story: name, counters, block state.</summary>
        private unsafe void DrawPidLabel(RainColumn col, int stride)
        {
            int x = col.Slot * _atlas.CellW + Math.Max(0, (_atlas.CellW - _atlas.LabelCellW) / 2);
            if (x + _atlas.LabelCellW > _w) return;

            string digits = col.Pid.ToString();
            if (digits.Length > PidLabelRows) digits = digits.Substring(digits.Length - PidLabelRows);

            byte* dstBase = (byte*)_bmp.BackBuffer;
            var font = col.Blocked ? _atlas.LabelDigitsBlocked : _atlas.LabelDigits;
            for (int i = 0; i < digits.Length; i++)
            {
                var glyph = font[digits[i] - '0'];
                int y = 3 + i * _atlas.LabelCellH;
                Blit(dstBase, stride, glyph, _atlas.LabelCellW, _atlas.LabelCellH, x, y);
            }
        }

        private void StepColumn(RainColumn col, double dt)
        {
            var now = DateTime.UtcNow;

            // A blocked PID stays visible even when silent: a slow red
            // heartbeat — a single letter every few seconds.
            if (col.Blocked && !col.Exited &&
                (now - col.LastActivityUtc).TotalSeconds > 4.0 &&
                (now - col.LastHeartbeatUtc).TotalSeconds > 4.0)
            {
                col.Budget = Math.Max(col.Budget, 1.0);
                col.LastHeartbeatUtc = now;
            }

            if (!col.Exited)
            {
                StepDrops(col, 1 + (int)(dt * 50), rx: false);
                if (_cfg.RxMode) StepDrops(col, Math.Max(1, (int)(dt * 25)), rx: true);
            }
        }

        private void StepDrops(RainColumn col, int maxAdvance, bool rx)
        {
            double budget = Math.Min(rx ? col.RxBudget : col.Budget, 600);
            int sameColor = 0;
            for (int i = col.Drops.Count - 1; i >= 0; i--)
                if (col.Drops[i].Rx == rx) sameColor++;

            if (budget >= 1.0 && sameColor < MaxDropsPerColor &&
                (sameColor == 0 || budget > 30))
            {
                col.Drops.Add(new RainDrop { Row = -1, Rx = rx });
                budget -= 1.0;
            }

            // Only the newest drop of each color carries the budget — traffic
            // is the story, and one live head per burst tells it.
            RainDrop lead = null;
            for (int i = col.Drops.Count - 1; i >= 0; i--)
                if (col.Drops[i].Rx == rx) { lead = col.Drops[i]; break; }

            int advance = lead == null ? 0 : (int)Math.Min(Math.Floor(budget), maxAdvance);
            budget -= advance;

            if (advance > 0)
            {
                var (headSet, trailSet) = GlyphSetsFor(col, rx);
                for (int i = 0; i < advance; i++)
                {
                    // The vacated head cell becomes fresh trail right away.
                    if (lead.Row >= 0) DrawCell(col, lead.Row, trailSet);
                    lead.Row++;
                    if (lead.Row >= _gridRows)
                    {
                        col.Drops.Remove(lead);
                        break;
                    }
                    DrawCell(col, lead.Row, headSet);

                    // Classic flicker: an occasional glyph mutates mid-trail.
                    if (lead.Row > 3 && _rand.NextDouble() < 0.22)
                        DrawCell(col, lead.Row - _rand.Next(2, Math.Min(12, lead.Row)), trailSet);
                }
            }

            if (rx) col.RxBudget = budget;
            else col.Budget = budget;
        }

        private (byte[][] head, byte[][] trail) GlyphSetsFor(RainColumn col, bool rx)
        {
            if (rx) return (_atlas.Rx, _atlas.Rx);
            return col.Blocked
                ? (_atlas.BlockedHead, _atlas.Blocked)
                : (_atlas.Head, _atlas.Trail);
        }

        private unsafe void DrawCell(RainColumn col, int row, byte[][] glyphSet)
        {
            if (row < 0 || row >= _gridRows) return;
            var glyph = glyphSet[_rand.Next(glyphSet.Length)];
            int x = col.Slot * _atlas.CellW;
            int y = _bandH + row * _atlas.CellH;
            if (x + _atlas.CellW > _w || y + _atlas.CellH > _h) return;
            Blit((byte*)_bmp.BackBuffer, _bmp.BackBufferStride, glyph,
                 _atlas.CellW, _atlas.CellH, x, y);
        }

        /// <summary>Source-over blend of a premultiplied glyph cell onto the
        /// surface (both Pbgra32, channel order identical).</summary>
        private static unsafe void Blit(byte* dstBase, int stride, byte[] src, int cw, int ch, int x, int y)
        {
            byte* dst = dstBase + y * stride + x * 4;
            int si = 0;
            for (int r = 0; r < ch; r++, dst += stride)
            {
                byte* d = dst;
                for (int c = 0; c < cw; c++, d += 4, si += 4)
                {
                    int sa = src[si + 3];
                    if (sa == 0) continue;
                    if (sa == 255)
                    {
                        d[0] = src[si]; d[1] = src[si + 1]; d[2] = src[si + 2]; d[3] = 255;
                        continue;
                    }
                    int inv = 255 - sa;
                    d[0] = (byte)(src[si] + ((d[0] * inv) >> 8));
                    d[1] = (byte)(src[si + 1] + ((d[1] * inv) >> 8));
                    d[2] = (byte)(src[si + 2] + ((d[2] * inv) >> 8));
                    d[3] = (byte)(sa + ((d[3] * inv) >> 8));
                }
            }
        }

        // ------------------------------------------------------------------
        // Hover
        // ------------------------------------------------------------------

        /// <summary>Human story of the column under a DIP x — the window
        /// turns it into a tooltip when the overlay takes mouse input.</summary>
        public string DescribeColumnAt(double dipX)
        {
            if (_atlas == null) return null;
            int slot = (int)((dipX * DpiScale) / _atlas.CellW);
            foreach (var col in _columns.Values)
                if (col.Slot == slot)
                    return
                        $"PID {col.Pid}  {col.Name}\n" +
                        $"tx {col.TxPackets:N0} pkts / {FormatBytes(col.TxBytes)}   " +
                        $"rx {col.RxPackets:N0} pkts / {FormatBytes(col.RxBytes)}\n" +
                        $"{col.Connections} tracked connection(s)" +
                        (col.Blocked ? "\nBlocked - raining red" : "") +
                        (col.Exited ? "\nprocess exited" : "");
            return null;
        }

        private static string FormatBytes(long b) => b switch
        {
            >= 1 << 30 => $"{b / (double)(1 << 30):F1} GB",
            >= 1 << 20 => $"{b / (double)(1 << 20):F1} MB",
            >= 1 << 10 => $"{b / (double)(1 << 10):F1} KB",
            _ => $"{b} B"
        };

        public void Dispose() => Stop();
    }
}
