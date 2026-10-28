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
    /// The rain itself, in two stacked layers: a dim ambient backdrop of
    /// glyphs on its own surface — steady brightness, because the trail
    /// fade never touches it, drifting down far slower than any stream —
    /// and the falling rain above it, drawn from the zLift-scaled atlas
    /// cells with a soft offset shadow so the streams float a plane above
    /// the backdrop. Every frame the rain layer fades toward transparent,
    /// then each column spends the letter budget its PID's transmits have
    /// banked — every TX packet buys one falling glyph step in that PID's
    /// column. A traffic-blocked PID spends its budget in red.
    /// </summary>
    public sealed class MatrixRenderer : IDisposable
    {
        private readonly Image _image;
        private readonly Image _ambientImage;
        private readonly MonitorEngine _engine;
        private readonly Random _rand = new();

        private MatrixRainConfig _cfg;
        private GlyphAtlas _atlas;
        private WriteableBitmap _bmp;
        private WriteableBitmap _bgBmp;      // the ambient backdrop layer

        // Backdrop state: a per-cell glyph grid, redrawn only when it
        // changes (a row drift or a random mutation) — never per frame.
        private int[,] _ambientGrid;         // [col,row] glyph index, -1 = empty
        private int _ambientCols, _ambientRows;
        private double _driftAccumSec, _ambientMutateSec;

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

        public MatrixRenderer(Image ambientImage, Image rainImage, MonitorEngine engine, MatrixRainConfig cfg)
        {
            _ambientImage = ambientImage;
            _image = rainImage;
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
            _bgBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
            _ambientImage.Source = _bgBmp;
            _ambientImage.Stretch = Stretch.Fill;
            RecomputeGrid();
        }

        private void RecomputeGrid()
        {
            if (_atlas == null || _w == 0) return;
            _bandH = _cfg.ShowLabels ? 3 + PidLabelRows * _atlas.LabelCellH + 3 : 0;
            _gridCols = Math.Max(1, _w / _atlas.CellW);
            _gridRows = Math.Max(1, (_h - _bandH) / _atlas.CellH);
            RebuildAmbientGrid();
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

            TickAmbient(dt);
        }

        // ------------------------------------------------------------------
        // Ambient backdrop — the slow, steady-brightness glyph field
        // ------------------------------------------------------------------

        /// <summary>(Re)builds the backdrop grid after a resize or an atlas
        /// rebuild; disabled ambient clears its surface.</summary>
        private void RebuildAmbientGrid()
        {
            if (_bgBmp == null || _atlas == null) return;
            if (!_cfg.Ambient)
            {
                _ambientGrid = null;
                _bgBmp.Lock();
                try
                {
                    unsafe { new Span<byte>((void*)_bgBmp.BackBuffer, _h * _bgBmp.BackBufferStride).Clear(); }
                    _bgBmp.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
                }
                finally { _bgBmp.Unlock(); }
                return;
            }

            _ambientCols = Math.Max(1, _w / _atlas.CellW);
            _ambientRows = Math.Max(1, (_h - _bandH) / _atlas.CellH);
            _ambientGrid = new int[_ambientCols, _ambientRows];
            for (int c = 0; c < _ambientCols; c++)
                for (int r = 0; r < _ambientRows; r++)
                    _ambientGrid[c, r] = _rand.NextDouble() <= _cfg.AmbientDensity
                        ? _rand.Next(_atlas.GlyphChars.Length) : -1;
            RedrawAmbientFull();
        }

        private unsafe void RedrawAmbientFull()
        {
            if (_bgBmp == null) return;
            _bgBmp.Lock();
            try
            {
                new Span<byte>((void*)_bgBmp.BackBuffer, _h * _bgBmp.BackBufferStride).Clear();
                if (_ambientGrid != null)
                {
                    byte* dstBase = (byte*)_bgBmp.BackBuffer;
                    int stride = _bgBmp.BackBufferStride;
                    for (int c = 0; c < _ambientCols; c++)
                        for (int r = 0; r < _ambientRows; r++)
                            BlitAmbientCell(dstBase, stride, c, r);
                }
                _bgBmp.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
            }
            finally { _bgBmp.Unlock(); }
        }

        private unsafe void BlitAmbientCell(byte* dstBase, int stride, int c, int r)
        {
            int idx = _ambientGrid[c, r];
            if (idx < 0) return;
            BlitClipped(dstBase, stride, _atlas.Ambient[idx], _atlas.CellW, _atlas.CellH,
                c * _atlas.CellW, _bandH + r * _atlas.CellH, _w, _h);
        }

        /// <summary>The backdrop's slow life: one row of drift per
        /// ambientDriftSeconds, and a few random glyph mutations between
        /// drifts. Runs on the ambient surface only — consistent brightness,
        /// because nothing here ever fades.</summary>
        private void TickAmbient(double dt)
        {
            if (!_cfg.Ambient || _bgBmp == null || _ambientGrid == null) return;
            _driftAccumSec += dt;
            _ambientMutateSec += dt;

            if (_driftAccumSec >= _cfg.AmbientDriftSeconds)
            {
                _driftAccumSec = 0;
                for (int c = 0; c < _ambientCols; c++)
                {
                    for (int r = _ambientRows - 1; r > 0; r--)
                        _ambientGrid[c, r] = _ambientGrid[c, r - 1];
                    _ambientGrid[c, 0] = _rand.NextDouble() <= _cfg.AmbientDensity
                        ? _rand.Next(_atlas.GlyphChars.Length) : -1;
                }
                RedrawAmbientFull();
                return;
            }

            if (_ambientMutateSec >= 0.12)
            {
                _ambientMutateSec = 0;
                _bgBmp.Lock();
                try
                {
                    unsafe
                    {
                        byte* dstBase = (byte*)_bgBmp.BackBuffer;
                        int stride = _bgBmp.BackBufferStride;
                        int n = 1 + _rand.Next(3);
                        for (int i = 0; i < n; i++)
                        {
                            int c = _rand.Next(_ambientCols), r = _rand.Next(_ambientRows);
                            _ambientGrid[c, r] = _rand.Next(_atlas.GlyphChars.Length);
                            // Clear the cell before the new glyph — drawing
                            // over the old one would muddy into a blob.
                            ClearCell(dstBase, stride, c * _atlas.CellW, _bandH + r * _atlas.CellH,
                                _atlas.CellW, _atlas.CellH);
                            BlitAmbientCell(dstBase, stride, c, r);
                        }
                    }
                    _bgBmp.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
                }
                finally { _bgBmp.Unlock(); }
            }
        }

        private static unsafe void ClearCell(byte* dstBase, int stride, int x, int y, int w, int h)
        {
            for (int r = 0; r < h; r++)
                new Span<byte>(dstBase + (y + r) * stride + x * 4, w * 4).Clear();
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

            string digits = col.Pid.ToString();
            if (digits.Length > PidLabelRows) digits = digits.Substring(digits.Length - PidLabelRows);

            byte* dstBase = (byte*)_bmp.BackBuffer;
            var font = col.Blocked ? _atlas.LabelDigitsBlocked : _atlas.LabelDigits;
            for (int i = 0; i < digits.Length; i++)
            {
                int y = 3 + i * _atlas.LabelCellH;
                BlitClipped(dstBase, stride, font[digits[i] - '0'],
                    _atlas.LabelCellW, _atlas.LabelCellH, x, y, _w, _h);
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
            if (rx) return (_atlas.RxLift, _atlas.RxLift);
            return col.Blocked
                ? (_atlas.BlockedHeadLift, _atlas.BlockedLift)
                : (_atlas.HeadLift, _atlas.TrailLift);
        }

        /// <summary>Draws one falling-glyph cell: the zLift-scaled glyph
        /// centered on its grid cell (so streams overlap their neighbors),
        /// with its shadow landing a couple of pixels down-right on the
        /// backdrop below — the depth cue that makes the stream float.</summary>
        private unsafe void DrawCell(RainColumn col, int row, byte[][] glyphSet)
        {
            if (row < 0 || row >= _gridRows) return;
            int gi = _rand.Next(glyphSet.Length);

            int x = col.Slot * _atlas.CellW + (_atlas.CellW - _atlas.LiftCellW) / 2;
            int y = _bandH + row * _atlas.CellH + (_atlas.CellH - _atlas.LiftCellH) / 2;
            byte* dstBase = (byte*)_bmp.BackBuffer;
            int stride = _bmp.BackBufferStride;

            if (_atlas.HasLift)
            {
                int so = Math.Max(1, (_atlas.LiftCellW - _atlas.CellW) / 2);
                BlitClipped(dstBase, stride, _atlas.ShadowLift[gi],
                    _atlas.LiftCellW, _atlas.LiftCellH, x + so, y + so + 1, _w, _h);
            }
            BlitClipped(dstBase, stride, glyphSet[gi],
                _atlas.LiftCellW, _atlas.LiftCellH, x, y, _w, _h);
        }

        /// <summary>Source-over blend of a premultiplied glyph cell onto the
        /// surface (both Pbgra32, channel order identical). copyW/copyH may
        /// be smaller than the source cell for edge clipping; srcW keeps the
        /// source rows aligned through the clipped columns.</summary>
        private static unsafe void Blit(byte* dstBase, int stride, byte[] src, int srcW, int copyW, int copyH, int x, int y)
        {
            byte* dst = dstBase + y * stride + x * 4;
            int si = 0;
            for (int r = 0; r < copyH; r++, dst += stride)
            {
                byte* d = dst;
                for (int c = 0; c < copyW; c++, d += 4, si += 4)
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
                si += (srcW - copyW) * 4;
            }
        }

        /// <summary>Blit with right/bottom clipping against the surface.</summary>
        private static unsafe void BlitClipped(byte* dstBase, int stride, byte[] src, int srcW, int srcH, int x, int y, int maxW, int maxH)
        {
            int w = Math.Min(srcW, maxW - x);
            int h = Math.Min(srcH, maxH - y);
            if (w <= 0 || h <= 0) return;
            Blit(dstBase, stride, src, srcW, w, h, x, y);
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
