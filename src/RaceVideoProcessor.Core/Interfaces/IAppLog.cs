namespace RaceVideoProcessor.Core.Interfaces;

public interface IAppLog
{
    event EventHandler<string>? LineWritten;
    string LogFilePath { get; }
    void Info(string message);
    void Error(string message);
}
