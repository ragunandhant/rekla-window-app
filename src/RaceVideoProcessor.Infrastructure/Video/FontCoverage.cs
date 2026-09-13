namespace RaceVideoProcessor.Infrastructure.Video;

/// <summary>
/// Answers "does this font file actually contain a glyph for this character?" by
/// reading the font's own character map.
///
/// Guessing from a file name is not good enough: a font called "segoeui" has no
/// Tamil, and FFmpeg does not fail when a glyph is missing — it draws an empty
/// box. The only reliable check is the 'cmap' table, which maps code points to
/// glyph ids; a code point that maps to glyph 0 (or not at all) is not covered.
///
/// Handles TrueType/OpenType files (.ttf, .otf) and collections (.ttc, where the
/// first face is used — the same face FFmpeg's fontfile= selects by default).
/// Reads only the table directory and the cmap table, never the whole file, so
/// scanning a Fonts folder with large CJK fonts in it stays fast.
/// </summary>
internal static class FontCoverage
{
    /// <summary>TAMIL LETTER KA — present in every font that genuinely covers Tamil.</summary>
    public const int TamilProbe = 0x0B95;

    private const int MaxCmapBytes = 8 * 1024 * 1024;

    public static bool CoversTamil(string path) => Covers(path, TamilProbe);

    public static bool Covers(string path, int codePoint)
    {
        try
        {
            using var stream = File.OpenRead(path);

            var header = ReadAt(stream, 0, 16);
            long fontOffset = 0;
            if (Tag(header, 0) == "ttcf")
            {
                if (U32(header, 8) == 0)
                    return false;
                fontOffset = U32(header, 12);
            }

            var directory = ReadAt(stream, fontOffset, 12);
            var tableCount = U16(directory, 4);
            if (tableCount == 0 || tableCount > 512)
                return false;

            var records = ReadAt(stream, fontOffset + 12, tableCount * 16);
            for (var i = 0; i < tableCount; i++)
            {
                var record = i * 16;
                if (Tag(records, record) != "cmap")
                    continue;

                // Table offsets are relative to the start of the file, including in collections.
                var cmapOffset = U32(records, record + 8);
                var cmapLength = (int)Math.Min(U32(records, record + 12), MaxCmapBytes);
                var cmap = ReadAt(stream, cmapOffset, cmapLength);
                return CmapCovers(cmap, codePoint);
            }

            return false;
        }
        catch
        {
            // Unreadable, truncated or not a font at all: treat as not covering.
            return false;
        }
    }

    private static bool CmapCovers(byte[] cmap, int codePoint)
    {
        var subtableCount = U16(cmap, 2);
        for (var i = 0; i < subtableCount; i++)
        {
            var record = 4 + i * 8;
            var platform = U16(cmap, record);
            var encoding = U16(cmap, record + 2);

            // Unicode platform, or Windows Unicode BMP (1) / full repertoire (10).
            var isUnicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (!isUnicode)
                continue;

            var subtable = (int)U32(cmap, record + 4);
            var format = U16(cmap, subtable);

            if (format == 4 && codePoint <= 0xFFFF && Format4Covers(cmap, subtable, codePoint))
                return true;
            if (format == 12 && Format12Covers(cmap, subtable, codePoint))
                return true;
        }

        return false;
    }

    /// <summary>Segment mapping to delta values — the common BMP format.</summary>
    private static bool Format4Covers(byte[] cmap, int subtable, int codePoint)
    {
        var segCountX2 = U16(cmap, subtable + 6);
        var endCodes = subtable + 14;
        var startCodes = endCodes + segCountX2 + 2;
        var idDeltas = startCodes + segCountX2;
        var idRangeOffsets = idDeltas + segCountX2;

        for (var i = 0; i < segCountX2 / 2; i++)
        {
            var end = U16(cmap, endCodes + i * 2);
            if (end < codePoint)
                continue;

            var start = U16(cmap, startCodes + i * 2);
            if (start > codePoint)
                return false;

            var delta = (short)U16(cmap, idDeltas + i * 2);
            var rangeOffsetPosition = idRangeOffsets + i * 2;
            var rangeOffset = U16(cmap, rangeOffsetPosition);

            int glyph;
            if (rangeOffset == 0)
            {
                glyph = (codePoint + delta) & 0xFFFF;
            }
            else
            {
                glyph = U16(cmap, rangeOffsetPosition + rangeOffset + (codePoint - start) * 2);
                if (glyph != 0)
                    glyph = (glyph + delta) & 0xFFFF;
            }

            return glyph != 0;
        }

        return false;
    }

    /// <summary>Segmented coverage — used by fonts that reach beyond the BMP.</summary>
    private static bool Format12Covers(byte[] cmap, int subtable, int codePoint)
    {
        var groups = U32(cmap, subtable + 12);
        for (long i = 0; i < groups; i++)
        {
            var group = subtable + 16 + (int)(i * 12);
            var start = U32(cmap, group);
            var end = U32(cmap, group + 4);
            if (codePoint >= start && codePoint <= end)
                return true;
        }
        return false;
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (count < 0 || offset < 0 || offset + count > stream.Length)
            throw new InvalidDataException("Font table lies outside the file.");

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
                throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }

    private static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];

    private static uint U32(byte[] b, int i)
        => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static string Tag(byte[] b, int i)
        => new(new[] { (char)b[i], (char)b[i + 1], (char)b[i + 2], (char)b[i + 3] });
}
