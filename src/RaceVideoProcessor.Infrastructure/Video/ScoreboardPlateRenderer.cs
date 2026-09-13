using SkiaSharp;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>What the scoreboard shows. There is no secondary cart number.</summary>
internal sealed record ScoreboardContent(string PrimaryName, string? PrimaryLocation, string CardNumber,
    string? SecondaryName, string? SecondaryLocation)
{
    public bool HasSecondary => !string.IsNullOrWhiteSpace(SecondaryName);
}

internal sealed record PlateStyle(string Accent, string TextColor, double Opacity, double FontScale,
    string TamilFontPath, string NumericFontPath);

/// <param name="RenderedText">The exact strings drawn, after fitting, keyed by role.</param>
internal sealed record PlateResult(byte[] Png, bool Truncated, IReadOnlyDictionary<string, string> RenderedText);

/// <summary>
/// Paints the "Elegant Prestige" graphics — the scoreboard panel and the closing
/// timing plaque — as transparent PNG plates that FFmpeg overlays on the video.
/// Text is drawn into the plate too, shaped with HarfBuzz (see ShapedTextRenderer),
/// because FFmpeg's drawtext mis-shapes Tamil that follows Latin initials.
///
/// drawbox can only fill flat rectangles, and the reference design is built from
/// gradients, radial lighting, curved highlights and glowing edges. Each layer
/// below is a direct translation of one CSS rule in
/// variant_3_elegant_prestige_exact_reference.html, painted in reference pixels
/// scaled by the plate height (the reference panel is 196 px tall), and composited
/// in the same stacking order as the CSS z-index.
///
/// No System.Drawing and no browser: the same output on every machine, and testable.
/// </summary>
internal static class ScoreboardPlateRenderer
{
    /// <summary>Height of the reference panel, which all reference pixel sizes are relative to.</summary>
    private const double ReferenceHeight = 196.0;

    /// <summary>Reference centre column width as a fraction of the panel width.</summary>
    public const double CenterColumnFraction = 0.164;

    private readonly record struct Rgb(double R, double G, double B)
    {
        public static Rgb Hex(int value) => new((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        public static Rgb Lerp(Rgb a, Rgb b, double t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    /// <summary>Straight-alpha RGBA canvas with source-over compositing.</summary>
    private sealed class Canvas
    {
        public readonly int Width;
        public readonly int Height;
        private readonly double[] _r, _g, _b, _a;

        public Canvas(int width, int height)
        {
            Width = width;
            Height = height;
            _r = new double[width * height];
            _g = new double[width * height];
            _b = new double[width * height];
            _a = new double[width * height];
        }

        public void Blend(int x, int y, Rgb color, double alpha)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || alpha <= 0)
                return;
            alpha = Math.Min(1, alpha);
            var i = y * Width + x;
            var outA = alpha + _a[i] * (1 - alpha);
            if (outA <= 0)
                return;
            _r[i] = (color.R * alpha + _r[i] * _a[i] * (1 - alpha)) / outA;
            _g[i] = (color.G * alpha + _g[i] * _a[i] * (1 - alpha)) / outA;
            _b[i] = (color.B * alpha + _b[i] * _a[i] * (1 - alpha)) / outA;
            _a[i] = outA;
        }

        /// <summary>Copies the canvas into a premultiplied Skia bitmap, ready for text.</summary>
        public SKBitmap ToBitmap()
        {
            var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var bytes = new byte[Width * Height * 4];
            for (var i = 0; i < Width * Height; i++)
            {
                var a = Math.Clamp(_a[i], 0, 1);
                bytes[i * 4] = ToByte(_r[i] * a);
                bytes[i * 4 + 1] = ToByte(_g[i] * a);
                bytes[i * 4 + 2] = ToByte(_b[i] * a);
                bytes[i * 4 + 3] = ToByte(a * 255);
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
            return bitmap;
        }

        private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
    }

    // ---- Palette (from the reference :root and rules) ---------------------------

    private static readonly Rgb BaseTop = Rgb.Hex(0x0b4a31);
    private static readonly Rgb BaseMid = Rgb.Hex(0x073522);
    private static readonly Rgb BaseBottom = Rgb.Hex(0x032016);
    private static readonly Rgb LightInner = new(21, 112, 73);
    private static readonly Rgb LightMid = new(15, 87, 57);
    private static readonly Rgb LightOuter = new(8, 56, 37);
    private static readonly Rgb CurveLine = new(94, 204, 150);
    private static readonly Rgb CurveGlow = new(61, 171, 118);
    private static readonly Rgb Border = new(86, 190, 139);
    private static readonly Rgb TopEdge = new(99, 221, 161);
    private static readonly Rgb InsetHighlight = new(119, 231, 175);
    private static readonly Rgb CenterTop = new(10, 63, 41);
    private static readonly Rgb CenterBottom = new(2, 28, 18);
    private static readonly Rgb CenterBorder = new(157, 218, 114);
    private static readonly Rgb MindaroEdge = Rgb.Hex(0xc7e947);
    private static readonly Rgb Black = new(0, 0, 0);

    /// <summary>The scoreboard panel with its text: primary left, cart number centre, secondary right.</summary>
    public static PlateResult RenderScoreboard(int width, int height, ScoreboardContent content, PlateStyle style)
    {
        var opacity = style.Opacity;
        var mindaro = style.Accent;
        var c = new Canvas(Math.Max(8, width), Math.Max(8, height));
        var s = c.Height / ReferenceHeight;
        var accent = ParseHex(mindaro, Rgb.Hex(0xd9f24f));

        PaintBase(c, s, opacity);
        PaintSideLights(c, lightWidthFraction: 0.30);
        PaintCurves(c, s);

        // .center — the card column, its gradient, inner shadows, borders and glow lines.
        var centerWidth = (int)Math.Round(c.Width * CenterColumnFraction);
        var centerX = (c.Width - centerWidth) / 2;
        for (var y = 0; y < c.Height; y++)
        {
            var t = y / (double)(c.Height - 1);
            var color = Rgb.Lerp(CenterTop, CenterBottom, t);
            var alpha = 0.72 + (0.80 - 0.72) * t;
            for (var x = centerX; x < centerX + centerWidth; x++)
            {
                c.Blend(x, y, color, alpha * opacity);
                // inset ±10px 0 22px rgba(0,0,0,.13)
                var edgeDistance = Math.Min(x - centerX, centerX + centerWidth - 1 - x);
                c.Blend(x, y, Black, 0.13 * Falloff(edgeDistance, 32 * s));
            }
        }

        var line = Px(s);
        for (var y = 0; y < c.Height; y++)
        {
            for (var k = 0; k < line; k++)
            {
                c.Blend(centerX + k, y, CenterBorder, 0.55);
                c.Blend(centerX + centerWidth - 1 - k, y, CenterBorder, 0.55);
            }
        }
        PaintCenterGlowLine(c, centerX, accent, s);
        PaintCenterGlowLine(c, centerX + centerWidth - 1, accent, s);

        PaintFrame(c, s, accent, centreGlowWidth: 120);

        using var bitmap = c.ToBitmap();
        using var canvas = new SKCanvas(bitmap);
        using var tamil = new ShapedTextRenderer(style.TamilFontPath);
        using var numeric = new ShapedTextRenderer(style.NumericFontPath);
        var text = ParseHex(style.TextColor, Rgb.Hex(0xf5f5ed));
        var white = ToSk(text);
        var sub = new SKColor(0xd9, 0xe1, 0xd9);
        var scale = (float)style.FontScale;
        var fs = (float)s;
        var rendered = new Dictionary<string, string>();
        var truncated = false;

        // .side: padding 0 7.5 % of the side column.
        var sideWidth = (c.Width - centerWidth) / 2f;
        var pad = sideWidth * 0.075f;
        var maxText = sideWidth - pad * 2;

        void Block(string key, string? name, string? location, bool alignRight)
        {
            // .name 45px/700, line-height 1.05; .location 21px/500 (set a little larger for video), margin-top 7px.
            var nameLine = tamil.Fit(name, maxText, 45 * fs * scale, 30 * fs * scale, bold: true);
            var locLine = tamil.Fit(location, maxText, 25 * fs * scale, 18 * fs * scale, bold: false);
            truncated |= nameLine.Truncated || locLine.Truncated;
            rendered[key + "-name"] = nameLine.Text;
            rendered[key + "-location"] = locLine.Text;

            var nameHeight = nameLine.Size * 1.05f;
            var gap = string.IsNullOrWhiteSpace(location) ? 0 : 7 * fs;
            var locHeight = string.IsNullOrWhiteSpace(location) ? 0 : locLine.Size;
            var top = (c.Height - (nameHeight + gap + locHeight)) / 2f;

            var nameX = alignRight ? c.Width - pad - nameLine.Width : pad;
            var locX = alignRight ? c.Width - pad - locLine.Width : pad;
            tamil.Draw(canvas, nameLine, nameX, Baseline(top, nameHeight, nameLine), white, bold: true, 2 * fs, 4 * fs, 89);
            if (locHeight > 0)
                tamil.Draw(canvas, locLine, locX, Baseline(top + nameHeight + gap, locHeight, locLine), sub, bold: false, 1 * fs, 3 * fs, 71);
        }

        Block("primary", content.PrimaryName, content.PrimaryLocation, alignRight: false);
        if (content.HasSecondary)
        {
            Block("secondary", content.SecondaryName, content.SecondaryLocation, alignRight: true);
        }
        else
        {
            // No secondary player: a quiet dash, never an invented name.
            var dash = numeric.Shape("—", 45 * fs * scale, bold: false);
            rendered["secondary-name"] = "—";
            numeric.Draw(canvas, dash, c.Width - pad - dash.Width, Baseline(0, c.Height, dash), sub, bold: false);
        }

        // .number: Inter/Arial 72px/800, centred in the card column.
        var number = numeric.Fit(content.CardNumber, centerWidth - 24 * fs, 72 * fs * scale, 44 * fs * scale, bold: true);
        truncated |= number.Truncated;
        rendered["card"] = number.Text;
        numeric.Draw(canvas, number, centerX + (centerWidth - number.Width) / 2f, Baseline(0, c.Height, number),
            white, bold: true, 2 * fs, 6 * fs, 102);

        return new PlateResult(Encode(bitmap), truncated, rendered);
    }

    /// <summary>Baseline that centres a line's ascent+descent box in a line box, as CSS line-height does.</summary>
    private static float Baseline(float lineTop, float lineHeight, ShapedTextRenderer.Line line)
        => lineTop + (lineHeight - (line.Ascent + line.Descent)) / 2f + line.Ascent;

    private static SKColor ToSk(Rgb rgb) => new((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// The closing timing plaque: the same package — base gradient, end lighting,
    /// frame, green top edge and mindaro bottom line — with the timing value.
    /// </summary>
    public static PlateResult RenderTimingPlaque(int width, int height, string timingText, PlateStyle style)
    {
        var opacity = style.Opacity;
        var mindaro = style.Accent;
        var c = new Canvas(Math.Max(8, width), Math.Max(8, height));
        var s = c.Height / ReferenceHeight;
        var accent = ParseHex(mindaro, Rgb.Hex(0xd9f24f));

        PaintBase(c, s, opacity);
        PaintSideLights(c, lightWidthFraction: 0.42);
        PaintCurves(c, s);

        // Short mindaro glow rules either side of the value, echoing the card column.
        PaintCenterGlowLine(c, (int)(c.Width * 0.08), accent, s);
        PaintCenterGlowLine(c, (int)(c.Width * 0.92), accent, s);

        PaintFrame(c, s, accent, centreGlowWidth: 90);

        using var bitmap = c.ToBitmap();
        using var canvas = new SKCanvas(bitmap);
        using var numeric = new ShapedTextRenderer(style.NumericFontPath);
        var fs = (float)s;
        var scale = (float)style.FontScale;

        var label = numeric.Shape("TIMING", 24 * fs * scale, bold: true);
        numeric.Draw(canvas, label, (c.Width - label.Width) / 2f, Baseline(0.10f * c.Height, 0.24f * c.Height, label), ToSk(accent), bold: true);

        var value = numeric.Fit(timingText, c.Width * 0.78f, 86 * fs * scale, 50 * fs * scale, bold: true);
        var valueTop = 0.30f * c.Height;
        var valueArea = c.Height - valueTop - (float)Math.Max(2, 4 * s) - 0.06f * c.Height;
        numeric.Draw(canvas, value, (c.Width - value.Width) / 2f, Baseline(valueTop, valueArea, value),
            ToSk(ParseHex(style.TextColor, Rgb.Hex(0xf5f5ed))), bold: true, 2 * fs, 6 * fs, 102);

        return new PlateResult(Encode(bitmap), value.Truncated,
            new Dictionary<string, string> { ["timing"] = value.Text, ["timing-label"] = "TIMING" });
    }

    // ---- Layers ----------------------------------------------------------------

    /// <summary>.scoreboard background gradient and its inset shadows.</summary>
    private static void PaintBase(Canvas c, double s, double opacity)
    {
        for (var y = 0; y < c.Height; y++)
        {
            var t = y / (double)(c.Height - 1);
            var color = t < 0.48 ? Rgb.Lerp(BaseTop, BaseMid, t / 0.48) : Rgb.Lerp(BaseMid, BaseBottom, (t - 0.48) / 0.52);
            // inset 0 -12px 30px rgba(0,0,0,.28): darkens toward the bottom edge.
            var bottomShade = 0.28 * Falloff(c.Height - 1 - y, 27 * s);
            // inset 0 1px 0 rgba(119,231,175,.12): a faint highlight under the top edge.
            var highlight = y >= Px(s) && y < Px(s) * 2 ? 0.12 : 0;

            for (var x = 0; x < c.Width; x++)
            {
                c.Blend(x, y, color, opacity);
                c.Blend(x, y, Black, bottomShade * opacity);
                c.Blend(x, y, InsetHighlight, highlight * opacity);
            }
        }
    }

    /// <summary>.side-light: radial green lighting at both ends, at 75 % layer opacity.</summary>
    private static void PaintSideLights(Canvas c, double lightWidthFraction)
    {
        var elementWidth = c.Width * lightWidthFraction;
        foreach (var leftSide in new[] { true, false })
        {
            // left: -7 %, ellipse centre at 10 %; right: mirrored.
            var elementX = leftSide ? -0.07 * c.Width : c.Width + 0.07 * c.Width - elementWidth;
            var cx = elementX + (leftSide ? 0.10 : 0.90) * elementWidth;
            var cy = 0.52 * c.Height;
            var rx = 0.70 * elementWidth;
            var ry = 1.05 * c.Height;

            var x0 = Math.Max(0, (int)Math.Floor(elementX));
            var x1 = Math.Min(c.Width, (int)Math.Ceiling(elementX + elementWidth));
            for (var y = 0; y < c.Height; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var dx = (x + 0.5 - cx) / rx;
                    var dy = (y + 0.5 - cy) / ry;
                    var r = Math.Sqrt(dx * dx + dy * dy);
                    var (color, alpha) = r switch
                    {
                        < 0.38 => (Rgb.Lerp(LightInner, LightMid, r / 0.38), 0.72 + (0.46 - 0.72) * (r / 0.38)),
                        < 0.62 => (Rgb.Lerp(LightMid, LightOuter, (r - 0.38) / 0.24), 0.46 + (0.10 - 0.46) * ((r - 0.38) / 0.24)),
                        < 0.76 => (LightOuter, 0.10 * (1 - (r - 0.62) / 0.14)),
                        _ => (LightOuter, 0.0)
                    };
                    c.Blend(x, y, color, alpha * 0.75);
                }
            }
        }
    }

    /// <summary>.curve: faint elliptical highlights sweeping in from both ends, at 22 % opacity.</summary>
    private static void PaintCurves(Canvas c, double s)
    {
        var width = 0.30 * c.Width;
        var rx = width / 2;
        var ry = 1.70 * c.Height / 2;
        var cy = -0.35 * c.Height + ry;
        var thickness = 2 * s;
        var glow = 30 * s;

        foreach (var leftSide in new[] { true, false })
        {
            var cx = leftSide ? -0.17 * c.Width + rx : c.Width + 0.17 * c.Width - rx;
            for (var y = 0; y < c.Height; y++)
            {
                var ny = (y + 0.5 - cy) / ry;
                if (Math.Abs(ny) >= 1)
                    continue;
                // Inner edge of the visible border on the curve's inward side.
                var edge = cx + (leftSide ? 1 : -1) * rx * Math.Sqrt(1 - ny * ny);
                var xStart = Math.Max(0, (int)(leftSide ? edge - thickness - 1 : edge - glow - 1));
                var xEnd = Math.Min(c.Width, (int)(leftSide ? edge + glow + 1 : edge + thickness + 1));
                for (var x = xStart; x < xEnd; x++)
                {
                    var inward = leftSide ? x + 0.5 - edge : edge - (x + 0.5);
                    if (inward >= -thickness && inward <= 0)
                        c.Blend(x, y, CurveLine, 0.55 * 0.22);
                    else if (inward > 0)
                        c.Blend(x, y, CurveGlow, 0.16 * 0.22 * Falloff(inward, glow));
                }
            }
        }
    }

    /// <summary>.center::before / ::after: a 2px mindaro glow line from 17 % to 83 % of the height.</summary>
    private static void PaintCenterGlowLine(Canvas c, int x, Rgb accent, double s)
    {
        var top = 0.17 * c.Height;
        var span = 0.66 * c.Height;
        var width = Math.Max(1, (int)Math.Round(2 * s));
        for (var y = (int)top; y < (int)(top + span); y++)
        {
            var p = (y - top) / span;
            var strength = p < 0.42 ? p / 0.42 : p > 0.58 ? (1 - p) / 0.42 : 1;
            for (var k = 0; k < width; k++)
                c.Blend(x - width / 2 + k, y, accent, 0.65 * strength);
        }
    }

    /// <summary>Outer border, .top-edge, .bottom-edge with its glow and the brighter centre point.</summary>
    private static void PaintFrame(Canvas c, double s, Rgb accent, int centreGlowWidth)
    {
        var line = Px(s);

        // border: 1px solid rgba(86,190,139,.72)
        for (var k = 0; k < line; k++)
        {
            for (var x = 0; x < c.Width; x++)
            {
                c.Blend(x, k, Border, 0.72);
                c.Blend(x, c.Height - 1 - k, Border, 0.72);
            }
            for (var y = 0; y < c.Height; y++)
            {
                c.Blend(k, y, Border, 0.72);
                c.Blend(c.Width - 1 - k, y, Border, 0.72);
            }
        }

        // .top-edge: 1px rgba(99,221,161,.75)
        for (var k = 0; k < line; k++)
            for (var x = 0; x < c.Width; x++)
                c.Blend(x, k, TopEdge, 0.75);

        // .bottom-edge: 4px, #c7e947 → mindaro → #c7e947, glow 0 -1px 5px rgba(217,242,79,.38)
        var bar = Math.Max(2, (int)Math.Round(4 * s));
        var barTop = c.Height - bar;
        var glow = 5 * s;
        for (var y = Math.Max(0, (int)(barTop - glow - 1)); y < barTop; y++)
        {
            var strength = 0.38 * Falloff(barTop - 1 - y, glow);
            for (var x = 0; x < c.Width; x++)
                c.Blend(x, y, accent, strength);
        }
        for (var y = barTop; y < c.Height; y++)
        {
            for (var x = 0; x < c.Width; x++)
            {
                var t = x / (double)(c.Width - 1);
                c.Blend(x, y, Rgb.Lerp(MindaroEdge, accent, 1 - Math.Abs(t * 2 - 1)), 1.0);
            }
        }

        // .bottom-edge::after: 120px × 8px radial mindaro highlight at the centre.
        var gw = centreGlowWidth * s;
        var gh = 8 * s;
        var gcx = c.Width / 2.0;
        var gcy = c.Height - gh / 2;
        for (var y = Math.Max(0, (int)(c.Height - gh)); y < c.Height; y++)
        {
            for (var x = Math.Max(0, (int)(gcx - gw / 2)); x < Math.Min(c.Width, (int)(gcx + gw / 2) + 1); x++)
            {
                var dx = (x + 0.5 - gcx) / (gw / 2);
                var dy = (y + 0.5 - gcy) / (gh / 2);
                var r = Math.Sqrt(dx * dx + dy * dy);
                if (r < 0.70)
                    c.Blend(x, y, accent, 0.50 * (1 - r / 0.70));
            }
        }
    }

    // ---- Helpers ---------------------------------------------------------------

    /// <summary>Linear falloff from 1 at distance 0 to 0 at <paramref name="radius"/>.</summary>
    private static double Falloff(double distance, double radius)
        => radius <= 0 ? 0 : Math.Clamp(1 - distance / radius, 0, 1);

    /// <summary>A 1-reference-pixel line at this scale, never thinner than one device pixel.</summary>
    private static int Px(double s) => Math.Max(1, (int)Math.Round(s));

    private static Rgb ParseHex(string? hex, Rgb fallback)
    {
        var clean = (hex ?? string.Empty).Trim().TrimStart('#');
        return clean.Length == 6 && int.TryParse(clean, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? Rgb.Hex(value)
            : fallback;
    }
}

