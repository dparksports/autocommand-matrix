using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace AutoCommand.Matrix
{
    /// <summary>
    /// The rain's type case: the configured font (an installed family, or
    /// the user's own .ttf/.otf file) rasterized once per color role into
    /// small Pbgra32 cell bitmaps, so a frame of rain is nothing but
    /// buffer copies — no text layout on the hot path.
    ///
    /// Monospace fonts (Consolas, Cascadia Mono, a bitmap terminal font…)
    /// look the most Matrix; proportional fonts work, each glyph centered
    /// in its cell.
    /// </summary>
    public sealed class GlyphAtlas
    {
        public string ResolvedFont;      // what actually loaded, for the tray balloon
        public int CellW, CellH;         // rain glyph cell, device px
        public int LabelCellW, LabelCellH; // PID-label cell (smaller), device px

        /// <summary>Rain glyph cells per role, indexed by position in
        /// <see cref="GlyphChars"/>.</summary>
        public byte[][] Trail, Head, Blocked, BlockedHead, Rx;

        /// <summary>Digits 0–9 for the column PID labels, per role
        /// (normal / blocked), indexed by digit value.</summary>
        public byte[][] LabelDigits, LabelDigitsBlocked;

        public string GlyphChars;

        /// <summary>The DPI scale the cells were rasterized at — the
        /// renderer rebuilds the atlas when the surface's scale differs.</summary>
        public double BuiltWithScale = 1.0;

        // ------------------------------------------------------------------

        public static GlyphAtlas Build(MatrixRainConfig cfg, double dpiScale)
        {
            var atlas = new GlyphAtlas { BuiltWithScale = dpiScale };
            var typeface = ResolveTypeface(cfg, out atlas.ResolvedFont);

            string chars = CharsetFor(cfg.Charset);
            atlas.GlyphChars = chars;

            double em = cfg.FontSizePx * dpiScale;
            double labelEm = Math.Max(7.0, cfg.FontSizePx * 0.62) * dpiScale;

            // Cell metrics from the widest glyph in the chosen set.
            double maxW = 1;
            foreach (var ch in chars)
                maxW = Math.Max(maxW, Measure(typeface, em, ch.ToString()));
            atlas.CellW = Math.Max(4, (int)Math.Ceiling(maxW) + 2);
            atlas.CellH = Math.Max(6, (int)Math.Ceiling(em * 1.35));

            atlas.LabelCellW = Math.Max(4, (int)Math.Ceiling(Measure(typeface, labelEm, "0")) + 2);
            atlas.LabelCellH = Math.Max(5, (int)Math.Ceiling(labelEm * 1.45));

            var c = cfg.Colors;
            atlas.Trail = chars.Select(ch => RenderGlyph(typeface, em, ch.ToString(), c.Trail, atlas.CellW, atlas.CellH)).ToArray();
            atlas.Head = chars.Select(ch => RenderGlyph(typeface, em, ch.ToString(), c.Head, atlas.CellW, atlas.CellH)).ToArray();
            atlas.Blocked = chars.Select(ch => RenderGlyph(typeface, em, ch.ToString(), c.Blocked, atlas.CellW, atlas.CellH)).ToArray();
            atlas.BlockedHead = chars.Select(ch => RenderGlyph(typeface, em, ch.ToString(), c.BlockedHead, atlas.CellW, atlas.CellH)).ToArray();
            atlas.Rx = chars.Select(ch => RenderGlyph(typeface, em, ch.ToString(), c.Rx, atlas.CellW, atlas.CellH)).ToArray();

            atlas.LabelDigits = Enumerable.Range(0, 10)
                .Select(d => RenderGlyph(typeface, labelEm, d.ToString(), c.Label, atlas.LabelCellW, atlas.LabelCellH)).ToArray();
            atlas.LabelDigitsBlocked = Enumerable.Range(0, 10)
                .Select(d => RenderGlyph(typeface, labelEm, d.ToString(), c.Blocked, atlas.LabelCellW, atlas.LabelCellH)).ToArray();

            return atlas;
        }

        /// <summary>
        /// Font resolution order: the configured font file (its internal
        /// family name is read straight out of the file via GlyphTypeface,
        /// so users never have to guess it), then the configured installed
        /// family, then Consolas — the rain always has a face.
        /// </summary>
        private static Typeface ResolveTypeface(MatrixRainConfig cfg, out string resolved)
        {
            if (!string.IsNullOrWhiteSpace(cfg.FontFile) && File.Exists(cfg.FontFile))
            {
                try
                {
                    var fileUri = new Uri("file:///" + Path.GetFullPath(cfg.FontFile).Replace('\\', '/'));
                    var gt = new GlyphTypeface(fileUri);
                    string name = null;
                    if (gt.FamilyNames != null)
                    {
                        if (!gt.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-us"), out name))
                            name = gt.FamilyNames.Values.FirstOrDefault();
                    }
                    name ??= Path.GetFileNameWithoutExtension(cfg.FontFile);

                    // FontFamily resolves a folder of fonts plus a family
                    // name — the documented loose-font-file form.
                    var folderUri = new Uri(fileUri, ".");
                    var ff = new FontFamily(folderUri, "./#" + name);
                    var tf = new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    if (tf.TryGetGlyphTypeface(out _))
                    {
                        resolved = $"{name} ({Path.GetFileName(cfg.FontFile)})";
                        return tf;
                    }
                }
                catch { /* unreadable font file — fall through to installed */ }
            }

            var family = new FontFamily(cfg.FontFamily);
            var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!face.TryGetGlyphTypeface(out _))
            {
                family = new FontFamily("Consolas");
                face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                resolved = $"Consolas (fallback — '{cfg.FontFamily}' not installed)";
            }
            else
            {
                resolved = family.Source + " (installed)";
            }
            return face;
        }

        private static double Measure(Typeface tf, double em, string text)
        {
            var ft = MakeText(tf, em, text, Colors.White);
            return ft.Width;
        }

        private static FormattedText MakeText(Typeface tf, double em, string text, Color color)
        {
            return new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, em, new SolidColorBrush(color), 1.0);
        }

        /// <summary>Renders one glyph centered in a transparent Pbgra32 cell
        /// and copies it out to a raw buffer the frame loop can blit.</summary>
        private static byte[] RenderGlyph(Typeface tf, double em, string text, string hexColor, int w, int h)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var ft = MakeText(tf, em, text, color);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawText(ft, new Point((w - ft.Width) / 2.0, (h - ft.Height) / 2.0));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var buf = new byte[w * h * 4];
            rtb.CopyPixels(buf, w * 4, 0);
            return buf;
        }

        /// <summary>The falling alphabet. Named sets or a literal string of
        /// characters (≥4, no spaces) for a custom alphabet.</summary>
        private static string CharsetFor(string spec)
        {
            const string latin = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            const string digits = "0123456789";
            const string symbols = "*+-/<>=|";
            // Half-width katakana — the movie's glyph set.
            string katakana = new string(Enumerable.Range(0xFF66, 0xFF9D - 0xFF66 + 1).Select(i => (char)i).ToArray());

            switch ((spec ?? "").Trim().ToLowerInvariant())
            {
                case "katakana": return Dedupe(katakana + latin + digits);
                case "latin": return Dedupe(latin + digits + symbols);
                case "digits": return digits;
                case "":
                case "katakana-latin": return Dedupe(katakana + latin + digits + symbols);
                default:
                    string trimmed = spec.Trim();
                    if (trimmed.Length >= 4 && !trimmed.Contains(' ')) return Dedupe(trimmed);
                    return Dedupe(katakana + latin + digits + symbols);
            }
        }

        private static string Dedupe(string s)
        {
            var seen = new HashSet<char>();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
                if (seen.Add(ch)) sb.Append(ch);
            return sb.ToString();
        }
    }
}
