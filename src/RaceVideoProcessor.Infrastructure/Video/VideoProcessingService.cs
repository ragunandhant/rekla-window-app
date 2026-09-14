using System.Diagnostics;
using System.Globalization;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Video;

public sealed class VideoProcessingService : IVideoProcessingService
{
    private readonly AppSettings _settings;
    private readonly IFfprobeService _ffprobe;
    private readonly IEncoderCapabilityService _encoderCapability;
    private readonly IOutputValidator _validator;
    private readonly FfmpegFilterBuilder _filterBuilder;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _processingGate = new(1, 1);

    public VideoProcessingService(
        AppSettings settings,
        IFfprobeService ffprobe,
        IEncoderCapabilityService encoderCapability,
        IOutputValidator validator,
        FfmpegFilterBuilder filterBuilder,
        IAppLog log)
    {
        _settings = settings;
        _ffprobe = ffprobe;
        _encoderCapability = encoderCapability;
        _validator = validator;
        _filterBuilder = filterBuilder;
        _log = log;
    }

    public async Task<ProcessingResult> ProcessAsync(
        ProcessingRequest request,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!await _processingGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new ProcessingResult(false, request.OutputPath, "Another video is already processing.", null, null, false);

        string? tempOutput = null;
        string? workDirectory = null;
        try
        {
            if (!File.Exists(request.InputPath))
                return new ProcessingResult(false, request.OutputPath, "Selected local video no longer exists.", null, null, false);

            var inputFull = Path.GetFullPath(request.InputPath);
            var outputFull = Path.GetFullPath(request.OutputPath);
            if (string.Equals(inputFull, outputFull, StringComparison.OrdinalIgnoreCase))
                return new ProcessingResult(false, request.OutputPath,
                    "Output path resolves to the original video. Choose a separate Processed folder.", null, null, false);

            var outputDirectory = Path.GetDirectoryName(outputFull);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                return new ProcessingResult(false, request.OutputPath, "Output folder is invalid.", null, null, false);

            Directory.CreateDirectory(outputDirectory);
            if (File.Exists(outputFull) && !request.AllowOverwrite)
                return new ProcessingResult(false, outputFull,
                    "A processed output with this filename already exists. Explicit overwrite confirmation is required.", null, null, false);

            var source = await _ffprobe.ProbeAsync(inputFull, cancellationToken).ConfigureAwait(false);
            var nvencAvailable = (await _encoderCapability.DetectAsync(cancellationToken).ConfigureAwait(false)).NvencAvailable;
            var useNvenc = _settings.EncoderPreference switch
            {
                EncoderPreference.CpuX264 => false,
                EncoderPreference.NvidiaNvenc => nvencAvailable,
                _ => nvencAvailable
            };

            if (_settings.EncoderPreference == EncoderPreference.NvidiaNvenc && !nvencAvailable)
                _log.Info("NVENC was requested but is unavailable; falling back to CPU / x264.");

            var extension = Path.GetExtension(outputFull);
            var baseName = Path.GetFileNameWithoutExtension(outputFull);
            tempOutput = Path.Combine(outputDirectory, $"{baseName}.processing-{Guid.NewGuid():N}{extension}");
            workDirectory = Path.Combine(Path.GetTempPath(), "RaceVideoProcessor", "jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);

            var filter = _filterBuilder.Build(source, request.Overlay, workDirectory);
            if (filter.Warning is not null)
                _log.Warning($"{request.CardNumber}: {filter.Warning}");

            var args = BuildProcessArguments(inputFull, tempOutput, filter, useNvenc, source);
            var targetBitRate = _settings.MatchSourceFileSize ? EncodingBudget.TargetVideoBitRate(source) : null;
            _log.Info(targetBitRate is { } kbps
                ? $"{request.CardNumber}: encoding at the source video bitrate, {kbps / 1000} kbps, to keep the file size " +
                  $"close to the original ({FormatSize(source.FileSizeBytes ?? new FileInfo(inputFull).Length)})."
                : $"{request.CardNumber}: source bitrate unavailable; using quality-based encoding.");
            _log.Info($"Processing {request.CardNumber} started using {(useNvenc ? "NVIDIA NVENC" : "CPU / x264")} " +
                      $"and font {filter.FontName}.");

            var processResult = await RunFfmpegWithProgressAsync(
                args, source.DurationSeconds, progress, cancellationToken).ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                SafeDelete(tempOutput);
                var error = "FFmpeg failed: " + ProcessRunner.Tail(processResult.StdErr);
                _log.Error($"{request.CardNumber}: {error}");
                return new ProcessingResult(false, outputFull, error, source, null, useNvenc);
            }

            var validation = await _validator.ValidateAsync(tempOutput, source, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                SafeDelete(tempOutput);
                var error = "Output validation failed: " + validation.Error;
                _log.Error($"{request.CardNumber}: {error}");
                return new ProcessingResult(false, outputFull, error, source, validation.OutputMetadata, useNvenc);
            }

            // Existing valid output remains untouched until the new temporary output has
            // passed FFprobe validation. Only then do we replace/move atomically enough
            // for the local filesystem workflow.
            LogSizeComparison(request.CardNumber, new FileInfo(inputFull).Length, new FileInfo(tempOutput).Length);

            File.Move(tempOutput, outputFull, overwrite: request.AllowOverwrite);
            tempOutput = null;
            _log.Info($"{request.CardNumber} output validation successful: {outputFull}");
            return new ProcessingResult(true, outputFull, null, source, validation.OutputMetadata, useNvenc);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempOutput);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(tempOutput);
            _log.Error($"{request.CardNumber} processing error: {ex.Message}");
            return new ProcessingResult(false, request.OutputPath, ex.Message, null, null, false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workDirectory))
                SafeDeleteDirectory(workDirectory);
            _processingGate.Release();
        }
    }

    internal List<string> BuildProcessArguments(string input, string output, FilterBuildResult filter, bool useNvenc, VideoMetadata source)
    {
        var args = new List<string> { "-hide_banner", "-y", "-i", input };
        AddOverlayInputs(args, filter);
        args.AddRange(["-filter_complex", filter.Filter, "-map", filter.OutputLabel, "-map", "0:a?"]);

        var veryHigh = _settings.EncodingQuality == EncodingQuality.VeryHigh;
        var target = _settings.MatchSourceFileSize ? EncodingBudget.TargetVideoBitRate(source) : null;

        if (target is { } bitRate)
        {
            // Size-matched: the source's own video bitrate, with a VBV cap so
            // complex moments may borrow bits but the average — and the file size —
            // stays at the source's. Quality comes from the encoder's search effort
            // (slow preset, lookahead, adaptive quantisation), not from extra bits.
            var maxRate = (bitRate * 3 / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bufSize = (bitRate * 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var average = bitRate.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (useNvenc)
            {
                args.AddRange([
                    "-c:v", "h264_nvenc",
                    "-preset", veryHigh ? "p7" : "p6",
                    "-tune", "hq",
                    "-rc", "vbr",
                    "-b:v", average,
                    "-maxrate", maxRate,
                    "-bufsize", bufSize,
                    "-spatial-aq", "1",
                    "-temporal-aq", "1",
                    "-rc-lookahead", "32",
                    "-profile:v", "high"
                ]);
            }
            else
            {
                args.AddRange([
                    "-c:v", "libx264",
                    "-preset", veryHigh ? "slow" : "medium",
                    "-b:v", average,
                    "-maxrate", maxRate,
                    "-bufsize", bufSize,
                    "-profile:v", "high"
                ]);
            }
        }
        else if (useNvenc)
        {
            var cq = veryHigh ? "16" : "18";
            args.AddRange([
                "-c:v", "h264_nvenc",
                "-preset", veryHigh ? "p7" : "p6",
                "-tune", "hq",
                "-rc", "vbr",
                "-cq", cq,
                "-b:v", "0",
                "-spatial-aq", "1",
                "-temporal-aq", "1",
                "-rc-lookahead", "32",
                "-profile:v", "high"
            ]);
        }
        else
        {
            var crf = veryHigh ? "16" : "18";
            args.AddRange([
                "-c:v", "libx264",
                "-preset", veryHigh ? "slow" : "medium",
                "-crf", crf,
                "-profile:v", "high"
            ]);
        }

        // The output uses the same container extension as the input and the audio
        // is not altered by the video filter, so stream-copying audio preserves it
        // without unnecessary quality loss.
        args.AddRange(["-c:a", "copy", "-map_metadata", "0"]);

        var extension = Path.GetExtension(output).ToLowerInvariant();
        if (extension is ".mp4" or ".mov" or ".m4v")
            args.AddRange(["-movflags", "+faststart"]);

        args.AddRange(["-progress", "pipe:1", "-nostats", output]);
        return args;
    }

    private void LogSizeComparison(string card, long sourceBytes, long outputBytes)
    {
        var percent = EncodingBudget.SizeDifferencePercent(sourceBytes, outputBytes);
        var line = $"{card}: size original {FormatSize(sourceBytes)} → processed {FormatSize(outputBytes)} " +
                   $"({(outputBytes >= sourceBytes ? "+" : "−")}{FormatSize(Math.Abs(outputBytes - sourceBytes))}, {percent:+0.0;-0.0}%).";
        if (_settings.MatchSourceFileSize && Math.Abs(percent) > _settings.FileSizeTolerancePercent)
            _log.Warning(line + $" Outside the {_settings.FileSizeTolerancePercent:0}% tolerance.");
        else
            _log.Info(line);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{bytes / 1024d:0} KB";

    private async Task<(int ExitCode, string StdErr)> RunFfmpegWithProgressAsync(
        IReadOnlyList<string> arguments,
        double durationSeconds,
        IProgress<ProcessingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfprobeService.ResolveToolPath(_settings.FfmpegPath, "ffmpeg"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start FFmpeg.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        });

        var stopwatch = Stopwatch.StartNew();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var processedTime = TimeSpan.Zero;

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (line.StartsWith("out_time=", StringComparison.Ordinal))
            {
                var value = line["out_time=".Length..];
                if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                    processedTime = parsed;
            }
            else if (line == "progress=end")
            {
                processedTime = TimeSpan.FromSeconds(durationSeconds);
            }

            if (line.StartsWith("out_time=", StringComparison.Ordinal) || line == "progress=end")
            {
                var ratio = durationSeconds <= 0 ? 0 : Math.Clamp(processedTime.TotalSeconds / durationSeconds, 0, 1);
                TimeSpan? eta = null;
                if (ratio > 0.01 && ratio < 1)
                {
                    var remainingSeconds = stopwatch.Elapsed.TotalSeconds * (1.0 / ratio - 1.0);
                    if (double.IsFinite(remainingSeconds) && remainingSeconds >= 0)
                        eta = TimeSpan.FromSeconds(remainingSeconds);
                }
                progress?.Report(new ProcessingProgress(ratio * 100.0, processedTime, stopwatch.Elapsed, eta));
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stderr);
    }

    private static void AddOverlayInputs(List<string> args, FilterBuildResult filter)
    {
        foreach (var overlay in filter.Inputs)
        {
            args.AddRange(overlay.InputOptions);
            args.AddRange(["-i", overlay.Path]);
        }
    }

    private static void SafeDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void SafeDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }
}
