using System.Net;
using RaceVideoProcessor.Core.Interfaces;

namespace RaceVideoProcessor.Infrastructure.Backend;

/// <summary>
/// A file streamed into an HTTP request body in chunks, reporting how many bytes
/// have actually been written to the connection. The file is never loaded into
/// memory, and the percentage is measured, not estimated.
/// </summary>
internal sealed class ProgressFileContent : HttpContent
{
    private const int ChunkSize = 256 * 1024;

    private readonly string _path;
    private readonly IProgress<UploadProgress>? _progress;
    private readonly long _length;

    public ProgressFileContent(string path, IProgress<UploadProgress>? progress)
    {
        _path = path;
        _progress = progress;
        _length = new FileInfo(path).Length;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
        var buffer = new byte[ChunkSize];
        long sent = 0;
        var lastReported = -1;

        _progress?.Report(new UploadProgress(0, _length));
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sent += read;

            // Report on whole-percent changes so a fast connection does not flood the UI.
            var percent = _length > 0 ? (int)(sent * 100 / _length) : 0;
            if (percent != lastReported)
            {
                lastReported = percent;
                _progress?.Report(new UploadProgress(sent, _length));
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
