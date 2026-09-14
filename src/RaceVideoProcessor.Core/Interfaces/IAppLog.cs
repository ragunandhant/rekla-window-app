namespace RaceVideoProcessor.Core.Interfaces;

public interface IAppLog
{
    event EventHandler<string>? LineWritten;
    string LogFilePath { get; }
    void Info(string message);

    /// <summary>Something the operator should notice that is not a failure.</summary>
    void Warning(string message);
    void Error(string message);
}
