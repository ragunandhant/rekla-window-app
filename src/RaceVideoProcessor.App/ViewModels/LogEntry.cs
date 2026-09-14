using System.Globalization;
using System.Text.RegularExpressions;

namespace RaceVideoProcessor.App.ViewModels;

/// <summary>One line of the application log, split into the Logs page's columns.</summary>
public sealed partial record LogEntry(string Time, string Level, string Stage, string Message)
{
    [GeneratedRegex(@"^\[(?<time>[^\]]+)\]\s+(?<level>INFO|WARNING|WARN|ERROR)\s+(?<message>.*)$")]
    private static partial Regex LinePattern();

    /// <summary>
    /// Parses "[2026-09-13 06:42:18] INFO  message". Older lines carry only a time.
    /// A line that does not match is kept whole as an INFO message rather than dropped.
    /// </summary>
    public static LogEntry Parse(string line)
    {
        var match = LinePattern().Match(line);
        if (!match.Success)
            return new LogEntry(string.Empty, "INFO", StageOf(line), line);

        var level = match.Groups["level"].Value == "WARN" ? "WARNING" : match.Groups["level"].Value;
        var time = match.Groups["time"].Value;
        if (DateTime.TryParseExact(time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            time = parsed.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var message = match.Groups["message"].Value;
        return new LogEntry(time, level, StageOf(message), message);
    }

    /// <summary>The workflow stage a message is about, from the words the application logs with.</summary>
    internal static string StageOf(string message)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("authentication") || m.Contains("login")) return "Authentication";
        if (m.Contains("assign")) return "Assignment";
        if (m.Contains("upload")) return "Upload";
        if (m.Contains("process") || m.Contains("ffmpeg") || m.Contains("encod") || m.Contains("nvenc") || m.Contains("output")) return "Processing";
        if (m.Contains("race")) return "Race";
        if (m.Contains("recovery")) return "Recovery";
        if (m.Contains("received") || m.Contains("poll") || m.Contains("sync") || m.Contains("entries")) return "Sync";
        if (m.Contains("selected") || m.Contains("next entry")) return "Selection";
        if (m.Contains("setting") || m.Contains("zoom") || m.Contains("switched")) return "Settings";
        return "Application";
    }
}
