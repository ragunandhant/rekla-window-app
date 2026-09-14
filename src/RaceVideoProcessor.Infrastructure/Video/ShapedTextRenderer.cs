using System.Globalization;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>
/// Shapes and draws scoreboard text with HarfBuzz and a CSS-style font fallback list.
///
/// Why not FFmpeg's drawtext: it shapes a whole line with one script, guessed from
/// the first letter, so Latin initials ("KS சிவராம்குமார்") made it shape the Tamil
/// as Latin and the vowel signs broke. Here every grapheme cluster is given to the
/// first font in the list that has all of its glyphs — exactly how a browser applies
/// <c>font-family: 'Noto Sans Tamil', 'Orbitron'</c> with Google Fonts' per-script
/// subsets — and each run is shaped by HarfBuzz as the script it is.
/// </summary>
internal sealed class ShapedTextRenderer : IDisposable
{
    private const int ShapeScale = 1024;

    private sealed class FontFace : IDisposable
    {
        public required SKTypeface Typeface { get; init; }
        public required Face Face { get; init; }
        public required HarfBuzzSharp.Font Font { get; init; }

        /// <summary>CSS unicode-range: the code points this face may be used for. Null means all it has.</summary>
        public Func<int, bool>? UnicodeRange { get; init; }

        public void Dispose()
        {
            Font.Dispose();
            Face.Dispose();
            Typeface.Dispose();
        }
    }

    private readonly List<FontFace> _faces = [];

    /// <param name="fontPaths">The fallback list, first choice first, like CSS <c>font-family</c>.</param>
    public ShapedTextRenderer(params string[] fontPaths)
    {
        if (fontPaths.Length == 0)
            throw new ArgumentException("At least one font is required.", nameof(fontPaths));

        foreach (var path in fontPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var typeface = SKTypeface.FromFile(path)
                           ?? throw new InvalidOperationException($"The font file could not be loaded: {Path.GetFileName(path)}");
            using var stream = typeface.OpenStream(out var ttcIndex);
            using var blob = stream.ToHarfBuzzBlob();
            var face = new Face(blob, ttcIndex) { UnitsPerEm = typeface.UnitsPerEm };
            var font = new HarfBuzzSharp.Font(face);
            font.SetScale(ShapeScale, ShapeScale);
            _faces.Add(new FontFace { Typeface = typeface, Face = face, Font = font, UnicodeRange = UnicodeRangeFor(path) });
        }
    }

    /// <summary>One line of text laid out at a size.</summary>
    public sealed class Line
    {
        internal readonly List<(int Face, ushort[] Glyphs, SKPoint[] Positions)> Runs = [];
        public string Text { get; init; } = string.Empty;
        public float Size { get; init; }
        public float Width { get; internal set; }

        /// <summary>Ascent and descent of the line's primary font, positive, in pixels.</summary>
        public float Ascent { get; init; }
        public float Descent { get; init; }
        public bool Overflows { get; internal set; }
    }

    /// <param name="letterSpacing">CSS letter-spacing: added after every character, the last included.</param>
    public Line Shape(string? text, float size, float letterSpacing = 0)
    {
        text ??= string.Empty;
        var primary = PrimaryFace();
        using var metricsFont = new SKFont(_faces[primary].Typeface, size);
        var metrics = metricsFont.Metrics;
        var line = new Line { Text = text, Size = size, Ascent = -metrics.Ascent, Descent = metrics.Descent };

        var x = 0f;
        var scale = size / ShapeScale;
        foreach (var (run, faceIndex) in SplitByFont(text))
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(run);
            buffer.GuessSegmentProperties();
            _faces[faceIndex].Font.Shape(buffer);

            var infos = buffer.GlyphInfos;
            var positions = buffer.GlyphPositions;
            var glyphs = new ushort[infos.Length];
            var points = new SKPoint[infos.Length];
            for (var i = 0; i < infos.Length; i++)
            {
                glyphs[i] = (ushort)infos[i].Codepoint;
                points[i] = new SKPoint(x + positions[i].XOffset * scale, -positions[i].YOffset * scale);
                x += positions[i].XAdvance * scale;

                // Spacing goes after the last glyph of each cluster.
                var clusterEnds = i == infos.Length - 1 || infos[i + 1].Cluster != infos[i].Cluster;
                if (clusterEnds)
                    x += letterSpacing;
            }
            line.Runs.Add((faceIndex, glyphs, points));
        }

        line.Width = x;
        return line;
    }

    /// <summary>
    /// The HTML's fitSingleLine(): start at <paramref name="maxSize"/> and step down
    /// by 0.5 px until the line fits or <paramref name="minSize"/> is reached. The
    /// line never wraps; <see cref="Line.Overflows"/> reports text that still does not fit.
    /// </summary>
    public Line Fit(string? text, float maxWidth, float maxSize, float minSize, float letterSpacing = 0)
    {
        text = (text ?? string.Empty).Trim();
        var size = maxSize;
        var line = Shape(text, size, letterSpacing);
        while (line.Width > maxWidth && size > minSize)
        {
            size -= 0.5f;
            line = Shape(text, size, letterSpacing);
        }
        line.Overflows = line.Width > maxWidth;
        return line;
    }

    /// <summary>Draws the line with its baseline at <paramref name="baselineY"/>.</summary>
    /// <param name="glowSigma">When above zero, a CSS text-shadow of this blur is drawn first in <paramref name="glow"/>.</param>
    public void Draw(SKCanvas canvas, Line line, float x, float baselineY, SKColor color,
        SKColor glow = default, float glowSigma = 0)
    {
        if (line.Runs.Count == 0)
            return;

        using var builder = new SKTextBlobBuilder();
        var fonts = new List<SKFont>();
        try
        {
            foreach (var (face, glyphs, positions) in line.Runs)
            {
                if (glyphs.Length == 0)
                    continue;
                var font = CreateFont(face, line.Size);
                fonts.Add(font);
                builder.AddPositionedRun(glyphs, font, positions);
            }

            using var blob = builder.Build();
            if (blob is null)
                return;

            if (glowSigma > 0)
            {
                using var shadow = new SKPaint
                {
                    IsAntialias = true,
                    Color = glow,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, glowSigma)
                };
                canvas.DrawText(blob, x, baselineY, shadow);
            }

            using var paint = new SKPaint { IsAntialias = true, Color = color };
            canvas.DrawText(blob, x, baselineY, paint);
        }
        finally
        {
            foreach (var font in fonts)
                font.Dispose();
        }
    }

    /// <summary>
    /// Splits text into runs that one font can render, first font in the list
    /// preferred. A grapheme cluster is never divided, so a Tamil consonant and its
    /// vowel sign always shape together.
    /// </summary>
    internal IEnumerable<(string Run, int Face)> SplitByFont(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        var current = new System.Text.StringBuilder();
        var currentFace = -1;

        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var face = FaceFor(element);
            if (currentFace >= 0 && face != currentFace)
            {
                yield return (current.ToString(), currentFace);
                current.Clear();
            }
            currentFace = face;
            current.Append(element);
        }

        if (current.Length > 0)
            yield return (current.ToString(), currentFace);
    }

    /// <summary>The first font containing every code point of the cluster; else the first font.</summary>
    private int FaceFor(string cluster)
    {
        var codepoints = new List<int>();
        for (var i = 0; i < cluster.Length; i += char.IsSurrogatePair(cluster, i) ? 2 : 1)
            codepoints.Add(char.ConvertToUtf32(cluster, i));

        for (var f = 0; f < _faces.Count; f++)
        {
            var typeface = _faces[f].Typeface;
            var range = _faces[f].UnicodeRange;
            // Joiners are default-ignorable: they need no glyph of their own.
            if (codepoints.All(cp => (range is null || range(cp)) && (cp is 0x200C or 0x200D || typeface.GetGlyph(cp) != 0)))
                return f;
        }
        return 0;
    }

    /// <summary>CSS's primary font: the first in the list whose range includes a space.</summary>
    private int PrimaryFace()
    {
        for (var f = 0; f < _faces.Count; f++)
        {
            if ((_faces[f].UnicodeRange?.Invoke(' ') ?? true) && _faces[f].Typeface.GetGlyph(' ') != 0)
                return f;
        }
        return 0;
    }

    /// <summary>
    /// Google Fonts serves Noto Sans Tamil as subsets, each limited by an @font-face
    /// unicode-range. The Tamil subset file also contains a space and punctuation,
    /// but a browser never uses it for them, so neither does this.
    /// </summary>
    private static Func<int, bool>? UnicodeRangeFor(string path)
        => Path.GetFileName(path).Contains(".tamil.", StringComparison.OrdinalIgnoreCase)
            ? cp => cp is (>= 0x0964 and <= 0x0965) or (>= 0x0B82 and <= 0x0BFA) or 0x200C or 0x200D or 0x20B9 or 0x25CC
            : null;

    private SKFont CreateFont(int face, float size) => new(_faces[face].Typeface, size)
    {
        Subpixel = true,
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None
    };

    public void Dispose()
    {
        foreach (var face in _faces)
            face.Dispose();
    }
}
