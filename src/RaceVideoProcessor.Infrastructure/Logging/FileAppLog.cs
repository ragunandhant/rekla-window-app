using RaceVideoProcessor.Core.Interfaces;

namespace RaceVideoProcessor.Infrastructure.Logging;

public sealed class FileAppLog : IAppLog
{
    private readonly object _gate = new();
    public event EventHandler<string>? LineWritten;
    public string LogFilePath { get; }

    public FileAppLog(string logFilePath)
    {
        LogFilePath = logFilePath;
        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {level,-5} {message}";
        lock (_gate)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        LineWritten?.Invoke(this, line);
    }
}
