using SkiaSharp;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>What the scoreboard shows. There is no secondary cart number.</summary>
internal sealed record ScoreboardContent(string PrimaryName, string? PrimaryLocation, string CardNumber,
    string? SecondaryName, string? SecondaryLocation)
{
    public bool HasSecondary => !string.IsNullOrWhiteSpace(SecondaryName);
}

/// <summary>
/// The typefaces the design HTML loads: player text uses
/// <c>'Noto Sans Tamil', 'Orbitron'</c> (Google serves Noto Sans Tamil as separate
/// Tamil and Latin subsets), the card number uses <c>'Orbitron'</c>.
/// </summary>
public sealed record ScoreboardFonts(string TamilText, string LatinText, string Orbitron)
{
    public const string Folder = "Fonts/Scoreboard";

    internal string[] PlayerStack => [TamilText, LatinText, Orbitron];
    internal string[] NumberStack => [Orbitron, LatinText];

    /// <summary>The bundled fonts next to the application, or null when any is missing.</summary>
    public static ScoreboardFonts? Bundled(string? baseDirectory = null)
    {
        var folder = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "Fonts", "Scoreboard");
        var fonts = new ScoreboardFonts(
            Path.Combine(folder, "NotoSansTamil-Medium.tamil.ttf"),
            Path.Combine(folder, "NotoSansTamil-Medium.latin.ttf"),
            Path.Combine(folder, "Orbitron-Bold.ttf"));
        return File.Exists(fonts.TamilText) && File.Exists(fonts.LatinText) && File.Exists(fonts.Orbitron) ? fonts : null;
    }
}

/// <param name="Png">A transparent RGBA plate, including room for the box shadows.</param>
/// <param name="BoxOffsetY">Where the boxes' top edge sits inside the plate, in video pixels.</param>
/// <param name="RenderedText">The exact strings drawn, keyed by role.</param>
internal sealed record PlateResult(byte[] Png, int Width, int Height, double BoxOffsetY, bool Overflowed,
    IReadOnlyDictionary<string, string> RenderedText);

/// <summary>
/// Paints the scoreboard from scorecard_center_number_matched_to_player_text.html
/// as transparent plates that FFmpeg overlays on the video.
///
/// Every number here is a CSS value from that file, in CSS pixels, for a 1920-px
/// wide canvas (the desktop rules; the 900 px and 500 px media queries never apply
/// to a video frame). The canvas is scaled by videoWidth / 1920, so a 1080p video
/// matches a browser rendering the page at 1920×1080 exactly, and 720p and 4K are
/// the same layout at device-pixel ratios 0.667 and 2.
///
///   .scorecard   width 95 %, bottom 4 %, centred; three boxes, 18 px gaps
///   .player-box  78 px tall, padding 0 18 px, gradient 135deg #0b150d → #040805,
///                1.5 px #14421b border, 6 px radius,
///                shadow 0 4px 15px rgba(0,0,0,.6), inset 0 1px 2px rgba(57,255,20,.1)
///   .player-text Noto Sans Tamil 500, 20 px, #a3e635, line-height 1.35,
///                padding 5 px 0 7 px, one line "name, location", shrinks 0.5 px to 6 px
///   .card-box    78 × 78, same surface
///   .card-number Orbitron 700, 34 px, #39ff14, letter-spacing .5 px, line-height 1,
///                text-shadow 0 0 8px rgba(57,255,20,.4); the page's fitting script
///                always ends at its 12 px minimum (see PaintNumberBox)
///
/// Rendered with Skia, which is also Chrome's rasteriser.
/// </summary>
internal static class ScoreboardPlateRenderer
{
    public const double CssCanvasWidth = 1920;

    private const float BoxHeight = 78;
    private const float Gap = 18;
    private const float PlayerPadding = 18;
    private const float Border = 1.5f;
    private const float Radius = 6;
    private const float ScorecardWidthShare = 0.95f;
    private const float PlayerFontSize = 20;
    private const float PlayerMinFontSize = 6;
    private const float PlayerLineHeight = 1.35f;
    private const float PlayerPaddingTop = 5;
    private const float PlayerPaddingBottom = 7;
    private const float CardFontSize = 34;
    private const float CardMinFontSize = 12;
    private const float CardLetterSpacing = 0.5f;

    /// <summary>Room around the boxes for "0 4px 15px" shadows, in CSS px.</summary>
    private const float ShadowMargin = 24;

    private static readonly SKColor GradientStart = SKColor.Parse("#0b150d");
    private static readonly SKColor GradientEnd = SKColor.Parse("#040805");
    private static readonly SKColor BorderColor = SKColor.Parse("#14421b");
    private static readonly SKColor PlayerTextColor = SKColor.Parse("#a3e635");
    private static readonly SKColor CardTextColor = SKColor.Parse("#39ff14");
    private static readonly SKColor CardGlow = new(57, 255, 20, (byte)Math.Round(0.4 * 255));
    private static readonly SKColor OuterShadow = new(0, 0, 0, (byte)Math.Round(0.6 * 255));
    private static readonly SKColor InsetShadow = new(57, 255, 20, (byte)Math.Round(0.1 * 255));

    /// <summary>The joined one-line text of a player box, as the HTML shows it: "name, location".</summary>
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
    /// </summary>
    public static PlateResult RenderScorecard(int videoWidth, int videoHeight, ScoreboardContent content, ScoreboardFonts fonts)
    {
        var s = (float)(videoWidth / CssCanvasWidth);
        var cssHeight = BoxHeight + ShadowMargin * 2;
        var plateHeight = (int)Math.Ceiling(cssHeight * s);

        using var bitmap = new SKBitmap(new SKImageInfo(videoWidth, plateHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(s);

        var cardWidthCss = (float)(CssCanvasWidth * ScorecardWidthShare);
        var left = (float)(CssCanvasWidth - cardWidthCss) / 2;
        var playerWidth = (cardWidthCss - Gap * 2 - BoxHeight) / 2;
        var top = ShadowMargin;

        var leftBox = SKRect.Create(left, top, playerWidth, BoxHeight);
        var cardBox = SKRect.Create(leftBox.Right + Gap, top, BoxHeight, BoxHeight);
        var rightBox = SKRect.Create(cardBox.Right + Gap, top, playerWidth, BoxHeight);

        var rendered = new Dictionary<string, string>();
        var overflowed = false;

        using (var player = new ShapedTextRenderer(fonts.PlayerStack))
        {
            var primary = PlayerLine(content.PrimaryName, content.PrimaryLocation);
            var secondary = content.HasSecondary ? PlayerLine(content.SecondaryName, content.SecondaryLocation) : "—";
            overflowed |= PaintPlayerBox(canvas, leftBox, primary, player);
            overflowed |= PaintPlayerBox(canvas, rightBox, secondary, player);
            rendered["primary"] = primary;
            rendered["secondary"] = secondary;
        }

        using (var number = new ShapedTextRenderer(fonts.NumberStack))
        {
            var card = content.CardNumber.Trim();
            overflowed |= PaintNumberBox(canvas, cardBox, card, number, CardFontSize, CardMinFontSize);
            rendered["card"] = card;
        }

        return new PlateResult(Encode(bitmap), videoWidth, plateHeight, top * s, overflowed, rendered);
    }

    /// <summary>Top of the scorecard plate in the frame: the boxes end 4 % above the bottom.</summary>
    public static int ScorecardPlateY(int videoWidth, int videoHeight)
    {
        var s = videoWidth / CssCanvasWidth;
        var boxBottom = videoHeight * (1 - 0.04);
        return (int)Math.Round(boxBottom - (BoxHeight + ShadowMargin) * s);
    }

    /// <summary>
    /// The closing timer. The HTML has no timer, so it is the HTML's own card box
    /// widened to its content — same surface, border, radius, shadows, Orbitron
    /// 700 34 px #39ff14 with the same glow — so it reads as part of the scorecard.
    /// </summary>
    public static PlateResult RenderTimer(int videoWidth, string timingText, ScoreboardFonts fonts)
    {
        var s = (float)(videoWidth / CssCanvasWidth);
        using var number = new ShapedTextRenderer(fonts.NumberStack);
        var text = timingText.Trim();
        var measured = number.Shape(text, CardFontSize, CardLetterSpacing);
        var boxWidth = Math.Max(BoxHeight, measured.Width + PlayerPadding * 2 + Border * 2);

        var cssWidth = boxWidth + ShadowMargin * 2;
        var cssHeight = BoxHeight + ShadowMargin * 2;
        var width = (int)Math.Ceiling(cssWidth * s);
        var height = (int)Math.Ceiling(cssHeight * s);

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(s);

        var box = SKRect.Create(ShadowMargin, ShadowMargin, boxWidth, BoxHeight);
        var overflowed = PaintNumberBox(canvas, box, text, number, CardFontSize, CardMinFontSize, padding: PlayerPadding, browserFit: false);
        return new PlateResult(Encode(bitmap), width, height, ShadowMargin * s, overflowed,
            new Dictionary<string, string> { ["timing"] = text });
    }

    // ---- Box surface ---------------------------------------------------------

    /// <summary>background, box-shadow (outer and inset), border and radius shared by every box.</summary>
    private static void PaintSurface(SKCanvas canvas, SKRect box)
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
    }

    // ---- Content -------------------------------------------------------------

    /// <summary>
    /// .player-text, laid out as Chrome lays it out (measured against the HTML at
    /// 1920×1080): fitSingleLine() shrinks while scrollWidth &gt; clientWidth − 2 of
    /// the box, i.e. while the text is wider than the box's inner width − 2 (851 px),
    /// so text may grow into the 18 px padding. Text that fits the 816 px element is
    /// centred; wider text starts at the element's left edge and is clipped at its
    /// right edge (overflow: hidden), exactly as the browser shows it.
    /// </summary>
    /// <returns>True when part of the text is clipped.</returns>
    private static bool PaintPlayerBox(SKCanvas canvas, SKRect box, string text, ShapedTextRenderer renderer)
    {
        PaintSurface(canvas, box);

        var inner = box.Width - Border * 2;
        var elementWidth = inner - PlayerPadding * 2;
        var line = renderer.Fit(text, inner - 2, PlayerFontSize, PlayerMinFontSize);

        // align-items: center on a block of line-height 1.35 plus 5 px / 7 px padding.
        var lineHeight = line.Size * PlayerLineHeight;
        var blockHeight = lineHeight + PlayerPaddingTop + PlayerPaddingBottom;
        var blockTop = box.Top + Border + (box.Height - Border * 2 - blockHeight) / 2;
        var baseline = blockTop + PlayerPaddingTop + (lineHeight - (line.Ascent + line.Descent)) / 2 + line.Ascent;
        var elementLeft = box.Left + Border + PlayerPadding;
        var x = line.Width <= elementWidth ? elementLeft + (elementWidth - line.Width) / 2 : elementLeft;

        canvas.Save();
        canvas.ClipRect(SKRect.Create(elementLeft, blockTop, elementWidth, blockHeight), antialias: true);
        renderer.Draw(canvas, line, x, baseline, PlayerTextColor);
        canvas.Restore();
        return line.Width > elementWidth;
    }

    /// <summary>
    /// .card-number, laid out as Chrome lays it out. The element is width: 100 % of
    /// the 75 px box interior, so its scrollWidth is never below 75 and the HTML's
    /// fitSingleLine() test (scrollWidth &gt; clientWidth − 2 = 73) is always true: the
    /// browser always shows the number at the 12 px minimum. That is reproduced here
    /// deliberately (<paramref name="browserFit"/>), because the HTML is the
    /// specification. The closing timer, which the HTML does not define, fits to its
    /// content instead.
    /// </summary>
    private static bool PaintNumberBox(SKCanvas canvas, SKRect box, string text, ShapedTextRenderer renderer,
        float maxSize, float minSize, float padding = 0, bool browserFit = true)
    {
        PaintSurface(canvas, box);

        var inner = box.Width - Border * 2;
        var elementWidth = inner - padding * 2;
        ShapedTextRenderer.Line line;
        if (browserFit)
        {
            var size = maxSize;
            line = renderer.Shape(text, size, CardLetterSpacing);
            while (Math.Max(elementWidth, line.Width) > inner - 2 && size > minSize)
            {
                size -= 0.5f;
                line = renderer.Shape(text, size, CardLetterSpacing);
            }
        }
        else
        {
            line = renderer.Fit(text, elementWidth, maxSize, minSize, CardLetterSpacing);
        }

        // line-height: 1, centred.
        var blockTop = box.Top + Border + (box.Height - Border * 2 - line.Size) / 2;
        var baseline = blockTop + (line.Size - (line.Ascent + line.Descent)) / 2 + line.Ascent;
        var elementLeft = box.Left + Border + padding;
        var x = line.Width <= elementWidth ? elementLeft + (elementWidth - line.Width) / 2 : elementLeft;

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(SKRect.Inflate(box, -Border, -Border), Radius - Border), antialias: true);
        // text-shadow: 0 0 8px rgba(57,255,20,.4) — blur 8 is sigma 4.
        renderer.Draw(canvas, line, x, baseline, CardTextColor, CardGlow, 4f);
        canvas.Restore();
        return line.Width > elementWidth;
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
