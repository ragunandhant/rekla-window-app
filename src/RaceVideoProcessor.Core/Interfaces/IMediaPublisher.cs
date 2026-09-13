using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Core.Interfaces;

/// <summary>Bytes of the upload body actually handed to the network so far.</summary>
public sealed record UploadProgress(long BytesSent, long? TotalBytes)
{
    /// <summary>Null when the total is unknown: show an indeterminate state, never a guess.</summary>
    public double? Percent => TotalBytes is > 0 ? Math.Clamp(BytesSent * 100.0 / TotalBytes.Value, 0, 100) : null;
}

/// <param name="Scope">Race ID and category of the entry, captured when the job started.</param>
public sealed record AssignmentRequest(RaceScope Scope, string PlayerId, string Marker, string VideoLink);

public enum BackendFailureKind
{
    /// <summary>Login failed, or the backend still answered 401 after logging in again.</summary>
    Authentication,
    Http,
    Network,
    InvalidResponse,
    Configuration
}

/// <summary>A backend call failed. The message never contains credentials or tokens.</summary>
public sealed class BackendException(BackendFailureKind kind, string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public BackendFailureKind Kind { get; } = kind;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Publishes a processed video: media upload, then assignment to the player.</summary>
public interface IMediaPublisher
{
    string Name { get; }

    /// <summary>Uploads the file as a stream and returns the hosted link.</summary>
    Task<string> UploadAsync(string filePath, IProgress<UploadProgress>? progress, CancellationToken cancellationToken);

    Task AssignAsync(AssignmentRequest request, CancellationToken cancellationToken);
}

public interface IBackendDiagnostics
{
    /// <summary>Logs in with the configured credentials and reports the outcome, without the token.</summary>
    Task<(bool Success, string Detail)> TestLoginAsync(CancellationToken cancellationToken);
}
