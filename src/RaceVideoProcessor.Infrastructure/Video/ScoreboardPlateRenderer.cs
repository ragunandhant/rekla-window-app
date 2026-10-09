using System.Globalization;
using SkiaSharp;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>What the scoreboard shows: names, locations and the cart number. No labels.</summary>
internal sealed record ScoreboardContent(string PrimaryName, string? PrimaryLocation, string CardNumber,
    string? SecondaryName, string? SecondaryLocation)
{
    public bool HasSecondary => !string.IsNullOrWhiteSpace(SecondaryName);
}

/// <summary>
/// The typefaces for the scoreboard. Barlow Condensed Bold is the primary
/// face for Latin text and digits; the Noto Sans Tamil subsets are fallback
/// only, used cluster-by-cluster for code points the display face does not
/// contain (Tamil script). Latin text and digits always render in the display face.
/// </summary>
public sealed record ScoreboardFonts(string TamilText, string LatinText, string Display)
{
    public const string Folder = "Fonts/Scoreboard";

    internal string[] PlayerStack => [TamilText, LatinText, Display];
    internal string[] NumberStack => [Display, LatinText];

    /// <summary>
    /// Display face first, Tamil subset as script fallback.
    /// <see cref="ShapedTextRenderer"/> gives each grapheme cluster to the first
    /// font containing all of its glyphs, so Tamil shapes in Noto Sans Tamil
    /// while everything else stays in the display face.
    /// </summary>
    internal string[] DigitalStack => [Display, LatinText, TamilText];

    /// <summary>The bundled fonts next to the application, or null when any is missing.</summary>
    public static ScoreboardFonts? Bundled(string? baseDirectory = null)
    {
        var folder = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Fonts", "Scoreboard");
        var fonts = new ScoreboardFonts(
            Path.Combine(folder, "NotoSansTamil-Medium.tamil.ttf"),
            Path.Combine(folder, "NotoSansTamil-Medium.latin.ttf"),
            Path.Combine(folder, "BarlowCondensed-Bold.ttf"));
        return File.Exists(fonts.TamilText) && File.Exists(fonts.LatinText) && File.Exists(fonts.Display) ? fonts : null;
    }
}

/// <param name="Png">A transparent RGBA plate, including room for the box shadows.</param>
/// <param name="BoxOffsetY">Where the boxes' top edge sits inside the plate, in video pixels.</param>
/// <param name="RenderedText">The exact strings drawn, keyed by role.</param>
internal sealed record PlateResult(byte[] Png, int Width, int Height, double BoxOffsetY, bool Overflowed,
    IReadOnlyDictionary<string, string> RenderedText);

/// <summary>
/// Paints the three-section digital-timer scoreboard as transparent plates that
/// FFmpeg overlays on the video: LEFT rectangle (primary "name, location"),
/// CENTRE panel (cart number only — or the final-seconds timing text),
/// RIGHT rectangle (secondary "name, location") — no labels anywhere. Each
/// side is one single line in one size, one weight and one treatment, centred
/// horizontally and vertically, the comma separating name from location.
///
/// The visual language is the Live Digital Timer HTML, applied to every box:
/// <c>linear-gradient(135deg, #0b150d, #040805)</c>, <c>1.5px solid #14421b</c>
/// border, <c>6px</c> radius, <c>0 4px 15px rgba(0,0,0,.6)</c> outer shadow with
/// an <c>inset 0 1px 2px rgba(57,255,20,.1)</c> highlight, Barlow Condensed 700
/// <c>#39ff14</c> text with a <c>0 0 8px rgba(57,255,20,.4)</c> glow. Side text
/// is the pure display cut (no synthetic emboldening);
/// centre content is always shaped
/// at one fixed size and its panel widens around longer content instead of
/// shrinking. Over-long side text shrinks, then truncates with a clean
/// ellipsis, never leaving its box.
///
/// Every number here is proportional to that HTML, in CSS pixels, for a
/// 1920-px wide canvas. The canvas is scaled by videoWidth / 1920, so a 1080p
/// video matches the proportions exactly, and 720p and 4K are the same layout
/// at device-pixel ratios 0.667 and 2.
///
/// A missing secondary player leaves its rectangle empty.
///
/// Rendered with Skia, which is also Chrome's rasteriser.
/// </summary>
internal static class ScoreboardPlateRenderer
{
    public const double CssCanvasWidth = 1920;

    private const float BoxHeight = 88;
    private const float Gap = 12;
    private const float PaddingH = 18;
    private const float Border = 1.5f;
    private const float Radius = 6;
    private const float ScorecardWidthShare = 0.95f;

    private const float NameSize = 28;
    private const float NameMinSize = 13;

    /// <summary>
    /// The cart/timer centre text is always shaped at this size — never shrunk
    /// to fit. Longer content widens the centre panel instead (see Layout).
    /// </summary>
    private const float CenterFixedSize = 32;
    private const float CenterPadding = 6;

    /// <summary>
    /// The HTML's <c>letter-spacing: 0.5px</c> at its 20 px size, as a ratio,
    /// so spacing stays proportional at video type sizes (28 px sides → 0.7).
    /// </summary>
    private const float LetterSpacingEm = 0.025f;
    private const float CartLetterSpacing = 0f;

    /// <summary>Room around the boxes for "0 4px 15px" shadows, in CSS px.</summary>
    private const float ShadowMargin = 24;

    private static readonly SKColor GradientStart = SKColor.Parse("#0b150d");
    private static readonly SKColor GradientEnd = SKColor.Parse("#040805");
    private static readonly SKColor BorderColor = SKColor.Parse("#14421b");
    private static readonly SKColor MainTextColor = SKColor.Parse("#39ff14");
    private static readonly SKColor TextGlow = new(57, 255, 20, (byte)Math.Round(0.4 * 255));
    private static readonly SKColor OuterShadow = new(0, 0, 0, (byte)Math.Round(0.6 * 255));
    private static readonly SKColor InsetShadow = new(57, 255, 20, (byte)Math.Round(0.1 * 255));

    /// <summary>The joined one-line text of a side rectangle: "name, location".</summary>
    public static string PlayerLine(string? name, string? location)
    {
        name = name?.Trim() ?? string.Empty;
        location = location?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "—";
        return location.Length == 0 ? name : $"{name}, {location}";
    }

    /// <summary>
    /// The full-width scorecard strip for a <paramref name="videoWidth"/> × <paramref name="videoHeight"/> frame.
    /// The plate spans the frame's width; overlay it at x = 0, y = <see cref="ScorecardPlateY"/>.
    /// When <paramref name="centerOverride"/> is given, the centre panel shows
    /// it instead of the cart number (the final-seconds timer), laid out by the
    /// same geometry so the panel simply widens around it.
    /// </summary>
    public static PlateResult RenderScorecard(int videoWidth, int videoHeight, ScoreboardContent content,
        ScoreboardFonts fonts, string? centerOverride = null)
    {
        var s = (float)(videoWidth / CssCanvasWidth);
        var cssHeight = BoxHeight + ShadowMargin * 2;
        var plateHeight = (int)Math.Ceiling(cssHeight * s);

        using var bitmap = new SKBitmap(new SKImageInfo(videoWidth, plateHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(s);

        using var text = new ShapedTextRenderer(fonts.DigitalStack);
        var centerText = (centerOverride ?? content.CardNumber).Trim();
        var (leftBox, centerBox, rightBox) = Layout(text, centerText);

        var rendered = new Dictionary<string, string>();
        var overflowed = false;

        {
            var line = PlayerLine(content.PrimaryName, content.PrimaryLocation);
            var (drawn, clipped) = PaintSideBox(canvas, leftBox, line, text);
            overflowed |= clipped;
            rendered["primary"] = drawn.Text;
            rendered["primaryLocation"] = (content.PrimaryLocation ?? string.Empty).Trim();
        }

        {
            var (drawn, clipped) = PaintCenterBox(canvas, centerBox, centerText, text);
            overflowed |= clipped;
            rendered["card"] = content.CardNumber.Trim();
            rendered["center"] = drawn.Text;
        }

        {
            var hasSecondary = content.HasSecondary;
            var line = hasSecondary ? PlayerLine(content.SecondaryName, content.SecondaryLocation) : string.Empty;
            string drawnText;
            if (hasSecondary)
            {
                var (drawn, clipped) = PaintSideBox(canvas, rightBox, line, text);
                overflowed |= clipped;
                drawnText = drawn.Text;
            }
            else
            {
                PaintSurface(canvas, rightBox);
                drawnText = "—";
            }
            rendered["secondary"] = drawnText;
            rendered["secondaryLocation"] = hasSecondary ? (content.SecondaryLocation ?? string.Empty).Trim() ?? string.Empty : string.Empty;
        }

        return new PlateResult(Encode(bitmap), videoWidth, plateHeight, ShadowMargin * s, overflowed, rendered);
    }

    /// <summary>
    /// The row geometry for centre content measured at the fixed size: the
    /// centre panel is at least square and widens around longer content, the
    /// side rectangles split the remainder symmetrically so the row stays
    /// balanced about the frame middle. Only pathological content (sides below
    /// <see cref="MinSideWidth"/>) widens the row itself.
    /// </summary>
    private const float MinSideWidth = 120;

    private static (SKRect Left, SKRect Center, SKRect Right) Layout(ShapedTextRenderer text, string centerText)
    {
        var rowWidth = (float)(CssCanvasWidth * ScorecardWidthShare);
        var centerMeasured = text.Shape(centerText, CenterFixedSize, CartLetterSpacing).Width;
        var centerW = Math.Max(BoxHeight, centerMeasured + CenterPadding * 2);
        var sideW = (rowWidth - centerW - Gap * 2) / 2;

        var left = (float)(CssCanvasWidth - rowWidth) / 2;
        if (sideW < MinSideWidth)
        {
            sideW = MinSideWidth;
            rowWidth = centerW + sideW * 2 + Gap * 2;
            left = (float)(CssCanvasWidth - rowWidth) / 2;
        }

        var top = ShadowMargin;
        var leftBox = SKRect.Create(left, top, sideW, BoxHeight);
        var centerBox = SKRect.Create(leftBox.Right + Gap, top, centerW, BoxHeight);
        var rightBox = SKRect.Create(centerBox.Right + Gap, top, sideW, BoxHeight);
        return (leftBox, centerBox, rightBox);
    }

    /// <summary>Top of the scorecard plate in the frame: the boxes end 4 % above the bottom.</summary>
    public static int ScorecardPlateY(int videoWidth, int videoHeight)
    {
        var s = videoWidth / CssCanvasWidth;
        var boxBottom = videoHeight * (1 - 0.04);
        return (int)Math.Round(boxBottom - (BoxHeight + ShadowMargin) * s);
    }

    // ---- Boxes -----------------------------------------------------------------

    /// <summary>
    /// One side rectangle: "Name, Location" on one single line, one size, one
    /// weight, one treatment — centred horizontally and vertically, no labels.
    /// Pure display-face 700 (no synthetic emboldening).
    /// The text shrinks to <see cref="NameMinSize"/>, then truncates cleanly.
    /// </summary>
    /// <returns>The drawn line and whether even the ellipsis did not fit.</returns>
    private static (ShapedTextRenderer.Line Line, bool Clipped) PaintSideBox(SKCanvas canvas, SKRect box,
        string line, ShapedTextRenderer text)
    {
        PaintSurface(canvas, box);

        var innerWidth = box.Width - PaddingH * 2;
        var drawn = FitSingleLine(text, line, innerWidth, NameSize, NameMinSize, NameSize * LetterSpacingEm);

        var x = box.Left + (box.Width - drawn.Width) / 2;
        // line-height: 1, centred vertically.
        var baseline = box.Top + (box.Height - drawn.Size) / 2 +
                       (drawn.Size - (drawn.Ascent + drawn.Descent)) / 2 + drawn.Ascent;

        canvas.Save();
        canvas.ClipRect(box, antialias: true);
        // text-shadow: 0 0 8px rgba(57,255,20,.4) — blur 8 is sigma 4.
        text.Draw(canvas, drawn, x, baseline, MainTextColor, TextGlow, 4f);
        canvas.Restore();

        return (drawn, drawn.Overflows);
    }

    /// <summary>
    /// Centre panel: its content (cart number, or the final-seconds timer)
    /// shaped once at <see cref="CenterFixedSize"/> — never shrunk, never
    /// compressed — centred on both axes. The panel was sized around it.
    /// </summary>
    private static (ShapedTextRenderer.Line Line, bool Clipped) PaintCenterBox(SKCanvas canvas, SKRect box,
        string content, ShapedTextRenderer text)
    {
        PaintSurface(canvas, box, focal: true);

        var line = text.Shape(content, CenterFixedSize, CartLetterSpacing);
        var innerWidth = box.Width - CenterPadding * 2;
        line.Overflows = line.Width > innerWidth;

        // line-height: 1, centred.
        var baseline = box.Top + (box.Height - line.Size) / 2 +
                       (line.Size - (line.Ascent + line.Descent)) / 2 + line.Ascent;
        var x = box.Left + (box.Width - line.Width) / 2;

        canvas.Save();
        canvas.ClipRect(box, antialias: true);
        // text-shadow: 0 0 8px rgba(57,255,20,.4) — blur 8 is sigma 4.
        text.Draw(canvas, line, x, baseline, MainTextColor, TextGlow, 4f);
        canvas.Restore();

        return (line, line.Overflows);
    }

    /// <summary>
    /// The HTML's single-line behaviour: shrink from <paramref name="maxSize"/>
    /// to <paramref name="minSize"/>, then truncate grapheme-safely with "…"
    /// so text never overflows, overlaps its neighbour or breaks the geometry.
    /// </summary>
    private static ShapedTextRenderer.Line FitSingleLine(ShapedTextRenderer text, string content,
        float maxWidth, float maxSize, float minSize, float letterSpacing)
    {
        var line = text.Fit(content, maxWidth, maxSize, minSize, letterSpacing);
        if (!line.Overflows)
            return line;

        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(content);
        while (enumerator.MoveNext())
            elements.Add((string)enumerator.Current);

        for (var n = elements.Count - 1; n >= 0; n--)
        {
            var candidate = string.Concat(elements.Take(n)) + "…";
            var shaped = text.Shape(candidate, minSize, letterSpacing);
            if (shaped.Width <= maxWidth)
                return shaped;
        }

        var dot = text.Shape("…", minSize, letterSpacing);
        dot.Overflows = dot.Width > maxWidth;
        return dot;
    }

    // ---- Container surface (the HTML's .digital-timer-container) ----------------

    /// <summary>
    /// background 135deg gradient, 1.5px #14421b border, 6px radius,
    /// 0 4px 15px black outer shadow, inset 0 1px 2px green highlight —
    /// plus two restrained premium touches in the same green language: a crisp
    /// 1 px glass edge-light just inside the top border, and (for the focal
    /// cart square and timer plaque) a soft green wash falling from the top.
    /// (No coloured outer aura: blurred translucent colour leaves
    /// unpremultiplied-fringe artefacts on the transparent plate.)
    /// </summary>
    private static readonly SKColor EdgeLight = new(190, 255, 170, 30);
    private static readonly SKColor FocalWash = new(57, 255, 20, 16);

    private static void PaintSurface(SKCanvas canvas, SKRect box, bool focal = false)
    {
        var outer = new SKRoundRect(box, Radius);

        // box-shadow: 0 4px 15px rgba(0,0,0,.6) — CSS blur radius 15 is a Gaussian sigma of 7.5.
        using (var shadow = new SKPaint
               {
                   IsAntialias = true,
                   Color = OuterShadow,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7.5f)
               })
        {
            var shadowRect = new SKRoundRect(SKRect.Create(box.Left, box.Top + 4, box.Width, box.Height), Radius);
            canvas.Save();
            canvas.ClipRoundRect(outer, SKClipOperation.Difference, antialias: true);
            canvas.DrawRoundRect(shadowRect, shadow);
            canvas.Restore();
        }

        // background: linear-gradient(135deg, #0b150d 0%, #040805 100%), painted to the border edge.
        var angle = 135 * Math.PI / 180;
        var dx = (float)Math.Sin(angle);
        var dy = (float)-Math.Cos(angle);
        var half = (Math.Abs(box.Width * dx) + Math.Abs(box.Height * dy)) / 2;
        var centre = new SKPoint(box.MidX, box.MidY);
        using (var background = new SKPaint
               {
                   IsAntialias = true,
                   Shader = SKShader.CreateLinearGradient(
                       new SKPoint(centre.X - dx * half, centre.Y - dy * half),
                       new SKPoint(centre.X + dx * half, centre.Y + dy * half),
                       [GradientStart, GradientEnd], [0f, 1f], SKShaderTileMode.Clamp)
               })
        {
            canvas.DrawRoundRect(outer, background);
        }

        if (focal)
        {
            // Soft green wash from the top edge: marks the cart square as the focus.
            using var wash = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(box.MidX, box.Top),
                    new SKPoint(box.MidX, box.Top + box.Height * 0.55f),
                    [FocalWash, SKColors.Transparent], [0f, 1f], SKShaderTileMode.Clamp)
            };
            canvas.Save();
            canvas.ClipRoundRect(outer, antialias: true);
            canvas.DrawRect(box, wash);
            canvas.Restore();
        }

        // box-shadow: inset 0 1px 2px rgba(57,255,20,.1), inside the padding box.
        var padding = new SKRoundRect(SKRect.Inflate(box, -Border, -Border), Radius - Border);
        using (var inset = new SKPaint
               {
                   IsAntialias = true,
                   Color = InsetShadow,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1f)
               })
        using (var ring = new SKPath { FillType = SKPathFillType.EvenOdd })
        {
            var hole = new SKRoundRect(SKRect.Create(padding.Rect.Left, padding.Rect.Top + 1, padding.Rect.Width, padding.Rect.Height), Radius - Border);
            ring.AddRect(SKRect.Inflate(padding.Rect, 20, 20));
            ring.AddRoundRect(hole);
            canvas.Save();
            canvas.ClipRoundRect(padding, antialias: true);
            canvas.DrawPath(ring, inset);
            canvas.Restore();
        }

        // border: 1.5px solid #14421b, inside the border box.
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Border,
            Color = BorderColor
        };
        canvas.DrawRoundRect(new SKRoundRect(SKRect.Inflate(box, -Border / 2, -Border / 2), Radius - Border / 2), stroke);

        // Glass edge-light: a crisp 1 px highlight just inside the top border.
        using var edge = new SKPaint { IsAntialias = false, Color = EdgeLight };
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(SKRect.Inflate(box, -Border, -Border), Radius - Border), antialias: true);
        canvas.DrawRect(SKRect.Create(box.Left, box.Top + Border, box.Width, 1), edge);
        canvas.Restore();
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
