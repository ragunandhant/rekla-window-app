using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Backend;

/// <summary>
/// Demo stand-in for the media upload and assignment, so the whole workflow can
/// be exercised without the backend. Nothing leaves the machine: the processed
/// file is read through in chunks — progress is the real read position — and a
/// clearly fake link is returned. Logged as DEMO so it is never mistaken for a real upload.
/// </summary>
public sealed class DemoMediaPublisher : IMediaPublisher
{
    private const int ChunkSize = 1024 * 1024;

    public string Name => "DEMO (nothing is uploaded)";

    public async Task<string> UploadAsync(string filePath, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
        var total = file.Length;
        var buffer = new byte[ChunkSize];
        long read = 0;
        int n;

        progress?.Report(new UploadProgress(0, total));
        while ((n = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            read += n;
            progress?.Report(new UploadProgress(read, total));
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }

        var name = Uri.EscapeDataString(Path.GetFileName(filePath));
        return $"https://demo.invalid/uploads/{name}";
    }

    public Task AssignAsync(AssignmentRequest request, CancellationToken cancellationToken)
        => Task.Delay(400, cancellationToken);
}

/// <summary>Chooses the demo or real publisher per call, following the data source setting.</summary>
public sealed class MediaPublisherRouter : IMediaPublisher
{
    private readonly AppSettings _settings;
    private readonly DemoMediaPublisher _demo;
    private readonly RaceBackendClient _real;

    public MediaPublisherRouter(AppSettings settings, DemoMediaPublisher demo, RaceBackendClient real)
    {
        _settings = settings;
        _demo = demo;
        _real = real;
    }

    private IMediaPublisher Current => _settings.DataSourceMode == DataSourceMode.Demo ? _demo : _real;

    public string Name => Current.Name;

    public Task<string> UploadAsync(string filePath, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
        => Current.UploadAsync(filePath, progress, cancellationToken);

    public Task AssignAsync(AssignmentRequest request, CancellationToken cancellationToken)
        => Current.AssignAsync(request, cancellationToken);
}
