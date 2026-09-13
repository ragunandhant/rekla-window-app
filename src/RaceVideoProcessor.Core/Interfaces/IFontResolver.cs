namespace RaceVideoProcessor.Core.Interfaces;

/// <summary>Outcome of locating a font file for the scoreboard.</summary>
/// <param name="Path">Absolute path to a .ttf/.otf, or null when nothing usable was found.</param>
/// <param name="Name">Human-readable font name for the settings screen.</param>
/// <param name="SupportsTamil">Whether the resolved font is known to cover the Tamil block.</param>
/// <param name="Source">Where it came from: configured, bundled or system.</param>
public sealed record FontResolution(string? Path, string Name, bool SupportsTamil, string Source);

/// <summary>
/// Finds a font file for FFmpeg's drawtext filter.
///
/// drawtext's <c>font=</c> lookup needs fontconfig, which Windows FFmpeg builds
/// generally lack, so an explicit <c>fontfile=</c> path is the only reliable
/// option. Race data is Tamil, so the font must cover the Tamil block.
/// </summary>
public interface IFontResolver
{
    FontResolution Resolve();
}
