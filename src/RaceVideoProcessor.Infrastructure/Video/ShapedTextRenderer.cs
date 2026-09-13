using System.Globalization;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>
/// Shapes and draws scoreboard text with HarfBuzz, so Tamil renders correctly.
///
/// Why not FFmpeg's drawtext: it shapes a whole line with one script, guessed from
/// the first letter. Real names mix scripts — "KS சிவராம்குமார்", "S கருப்புசாமி" —
/// so the Latin initials made FFmpeg shape the Tamil as Latin: vowel signs were not
/// reordered or combined and the name rendered wrong. Here the text is split into
/// runs by script and each run is shaped as what it is, exactly as a browser does.
/// </summary>
internal sealed class ShapedTextRenderer : IDisposable
{
    private readonly SKTypeface _typeface;
    private readonly Face _face;
    private readonly HarfBuzzSharp.Font _font;
    private const int ShapeScale = 512;

    public ShapedTextRenderer(string fontPath)
    {
        _typeface = SKTypeface.FromFile(fontPath)
                    ?? throw new InvalidOperationException($"The font file could not be loaded: {Path.GetFileName(fontPath)}");
        using var stream = _typeface.OpenStream(out var ttcIndex);
        using var blob = stream.ToHarfBuzzBlob();
        _face = new Face(blob, ttcIndex) { UnitsPerEm = _typeface.UnitsPerEm };
        _font = new HarfBuzzSharp.Font(_face);
        _font.SetScale(ShapeScale, ShapeScale);
    }

    /// <summary>One line of text laid out at a size: glyph runs, advance width and vertical metrics.</summary>
    public sealed class Line
    {
        internal readonly List<(ushort[] Glyphs, SKPoint[] Positions)> Runs = [];
        public string Text { get; init; } = string.Empty;
        public float Size { get; init; }
        public float Width { get; internal set; }
        public float Ascent { get; init; }
        public float Descent { get; init; }
        public bool Truncated { get; init; }
    }

    public Line Shape(string? text, float size, bool bold)
    {
        text ??= string.Empty;
        using var skFont = CreateFont(size, bold);
        var metrics = skFont.Metrics;
        var line = new Line { Text = text, Size = size, Ascent = -metrics.Ascent, Descent = metrics.Descent };

        var x = 0f;
        var scale = size / ShapeScale;
        foreach (var (run, script) in SplitByScript(text))
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(run);
            buffer.Direction = Direction.LeftToRight;
            buffer.Script = script;
            buffer.Language = new Language(script == Script.Tamil ? "ta" : "en");
            _font.Shape(buffer);

            var infos = buffer.GlyphInfos;
            var positions = buffer.GlyphPositions;
            var glyphs = new ushort[infos.Length];
            var points = new SKPoint[infos.Length];
            for (var i = 0; i < infos.Length; i++)
            {
                glyphs[i] = (ushort)infos[i].Codepoint;
                points[i] = new SKPoint(x + positions[i].XOffset * scale, -positions[i].YOffset * scale);
                x += positions[i].XAdvance * scale;
            }
            line.Runs.Add((glyphs, points));
        }

        // Synthetic emboldening widens each glyph slightly beyond its advance.
        line.Width = x + (bold ? size * 0.02f : 0);
        return line;
    }

    /// <summary>
    /// The largest size from <paramref name="preferred"/> down to <paramref name="minimum"/>
    /// at which the text fits; if it still overflows, whole grapheme clusters are
    /// removed from the end and an ellipsis added — never a cut inside a Tamil cluster.
    /// </summary>
    public Line Fit(string? text, float maxWidth, float preferred, float minimum, bool bold)
    {
        text = (text ?? string.Empty).Trim();
        var step = Math.Max(0.5f, preferred * 0.04f);
        for (var size = preferred; size >= minimum; size -= step)
        {
            var line = Shape(text, size, bold);
            if (line.Width <= maxWidth)
                return line;
        }

        var info = new StringInfo(text);
        for (var keep = info.LengthInTextElements - 1; keep > 0; keep--)
        {
            var candidate = info.SubstringByTextElements(0, keep).TrimEnd() + "…";
            var line = Shape(candidate, minimum, bold);
            if (line.Width <= maxWidth)
                return new Line { Text = candidate, Size = minimum, Ascent = line.Ascent, Descent = line.Descent, Truncated = true }
                    .WithRuns(line);
        }
        return Shape("…", minimum, bold);
    }

    /// <summary>Draws the line with its baseline at <paramref name="baselineY"/>, with an optional soft shadow.</summary>
    public void Draw(SKCanvas canvas, Line line, float x, float baselineY, SKColor color, bool bold,
        float shadowOffsetY = 0, float shadowBlur = 0, byte shadowAlpha = 0)
    {
        if (line.Runs.Count == 0)
            return;

        using var skFont = CreateFont(line.Size, bold);
        using var builder = new SKTextBlobBuilder();
        foreach (var (glyphs, positions) in line.Runs)
        {
            if (glyphs.Length > 0)
                builder.AddPositionedRun(glyphs, skFont, positions);
        }
        using var blob = builder.Build();
        if (blob is null)
            return;

        if (shadowAlpha > 0)
        {
            using var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, shadowAlpha),
                MaskFilter = shadowBlur > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadowBlur / 2) : null
            };
            canvas.DrawText(blob, x, baselineY + shadowOffsetY, shadow);
        }

        using var paint = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawText(blob, x, baselineY, paint);
    }

    private SKFont CreateFont(float size, bool bold) => new(_typeface, size)
    {
        Embolden = bold,
        Subpixel = true,
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None
    };

    /// <summary>
    /// Splits text into runs of one script. Spaces, punctuation and marks belong to
    /// the run they sit in, so "KS சிவராம்குமார்" becomes "KS " (Latin) and the
    /// Tamil name (Tamil).
    /// </summary>
    internal static IEnumerable<(string Run, Script Script)> SplitByScript(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        var current = new System.Text.StringBuilder();
        Script? currentScript = null;

        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var script = ScriptOf(element);
            if (script is null || currentScript is null || script == currentScript)
            {
                currentScript ??= script;
                current.Append(element);
                continue;
            }

            yield return (current.ToString(), currentScript.Value);
            current.Clear().Append(element);
            currentScript = script;
        }

        if (current.Length > 0)
            yield return (current.ToString(), currentScript ?? Script.Latin);
    }

    /// <summary>Tamil, Latin, or null for characters that take the script of their neighbours.</summary>
    private static Script? ScriptOf(string element)
    {
        var c = element[0];
        if (c is >= '஀' and <= '௿')
            return Script.Tamil;
        return char.IsLetterOrDigit(c) ? Script.Latin : null;
    }

    public void Dispose()
    {
        _font.Dispose();
        _face.Dispose();
        _typeface.Dispose();
    }
}

internal static class LineExtensions
{
    public static ShapedTextRenderer.Line WithRuns(this ShapedTextRenderer.Line target, ShapedTextRenderer.Line source)
    {
        target.Runs.AddRange(source.Runs);
        target.Width = source.Width;
        return target;
    }
}
