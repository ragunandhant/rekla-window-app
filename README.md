# Race Video Processor

Production-oriented Windows WPF application for the workflow:

**new entry arrives → user maps one local video → preview → user explicitly starts FFmpeg → validate → save to Processed with the same filename**.

There is intentionally no automatic bulk-processing path.

## What is implemented

- **The entry is the primary player's card number** (`player.cartNo`, e.g. `1000AAA`). The application never invents a sequential entry number, and there is no secondary card number.
- **Two-panel workspace**: a permanently visible, searchable, filterable entry list on the left; the selected entry's workspace on the right. Navigation is never blocked — an entry with no video, a failed entry and a rendering entry are all equally clickable, and every entry keeps its own mapped video, status, output and error.
- **Built for sunlight**: high-contrast light theme, near-black text, solid borders, 12px minimum type, status carried by glyph as well as colour.
- **Tamil throughout** — UI, entry list, preview and the rendered video — via a Tamil-capable font stack and an explicit `fontfile=` for FFmpeg, with a bundled Noto Sans Tamil and a Nirmala UI fallback.
- **Broadcast scoreboard**: accent-plated card number at centre, primary left, secondary right (omitted when there is none), scaled proportionally from 720p to 4K.
- Standard Windows window: minimise, maximise/restore, close, resize, snap, and remembered size and position.
- A render belongs to the entry it started on, so navigating away mid-render is safe; `CANCEL RENDER` stops it without touching the source video.
- Every unavailable action states the reason next to the button.
- Async background polling at 10 / 20 / 30 / 60 seconds.
- Fully functional Demo Mode: 100 deterministic entries with realistic card numbers and Tamil names, released one at a time.
- Dedicated configurable Demo Video Folder; the same sample clips can be explicitly mapped to many different demo entries.
- `Simulate Next Entry` for immediate workflow testing.
- Real API adapter mapping the supplied payload (`player`, `secondaryPlayer`, `timings`, `status`, `raceId[].types`, `marker`), tolerant of nulls and missing fields.
- Explicit Windows video file selection (`mp4`, `mkv`, `mov`, `avi`, `m4v`, `ts`, `mts`, `m2ts`).
- SQLite persistence for local mapping, status, output, errors, settings and sync metadata.
- Duplicate protection by stable identity (`marker` / `playerId`), independent of the displayed card number.
- Overlay-data hash and `OUTDATED` detection after a completed render.
- FFprobe source inspection and output validation.
- Resolution-aware broadcast-style scoreboard.
- Long Tamil names shrink to fit before they are ever shortened.
- Race timing formatted from the API's seconds value (22.5 → `00:22.50`) in a centred plaque during the final N seconds.
- Actual FFmpeg progress and ETA.
- Real NVENC hardware test plus x264 fallback.
- High-quality NVENC/x264 settings; no forced resolution or frame-rate reduction.
- Audio stream copy where present.
- Temporary-output validation before promotion to the final filename.
- Two-frame real preview: normal portion and final portion.
- Local rolling UI log plus persisted log file.
- Unit tests plus opt-in real-FFmpeg integration test.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full design and [`docs/VALIDATION.md`](docs/VALIDATION.md) for the checks performed in the generation environment.

## Prerequisites for development

On Windows:

1. Visual Studio 2022 with **.NET desktop development**, or the .NET 8 SDK.
2. A full FFmpeg build that contains `drawtext`, `libx264`, FFprobe, and (for GPU use) `h264_nvenc`.
3. NVIDIA driver installed for NVENC hardware acceleration when using an RTX-class GPU.

The finished published app does **not** require Python or Node.js.

## Run from source

```powershell
dotnet restore .\RaceVideoProcessor.sln
dotnet build .\RaceVideoProcessor.sln -c Release
dotnet run --project .\src\RaceVideoProcessor.App\RaceVideoProcessor.App.csproj -c Release
```

If `ffmpeg.exe` and `ffprobe.exe` are on PATH, the defaults work. You can also configure absolute paths in Settings.

The application additionally checks for bundled tools at:

```text
<application folder>\tools\ffmpeg\ffmpeg.exe
<application folder>\tools\ffmpeg\ffprobe.exe
```

## First end-to-end demo

1. Launch the app. Demo Mode is the default.
2. Entry 001 appears.
3. Select an actual local video.
4. Click **PREVIEW** to render representative overlay frames with FFmpeg.
5. Click **START PROCESSING**.
6. Watch actual FFmpeg progress.
7. After FFmpeg exits, FFprobe validates the temporary output.
8. The valid file is moved to the configured `Processed` folder with the original filename.
9. Click **SIMULATE NEXT ENTRY**.
10. Entry 002 appears; map and process it independently.

Demo videos may be reused across entries. The mapping remains explicit per EntryId.

## Real API integration

Do not modify the UI, polling logic or processing service. Implement:

```text
IRealApiPayloadAdapter
```

in `RaceVideoProcessor.Infrastructure/Providers` using the actual documented JSON contract, then replace this registration in `App.xaml.cs`:

```csharp
services.AddSingleton<IRealApiPayloadAdapter, UnconfiguredRealApiPayloadAdapter>();
```

with your real adapter.

## Tests

```powershell
dotnet test .\RaceVideoProcessor.sln -c Release
```

The tests cover mock arrival, duplicate IDs, data changes/outdated outputs, HTTP success/failure/timeout, SQLite restart persistence, long-name fitting, common resolutions, output validation and missing-video failure.

An opt-in integration test performs real FFmpeg processing:

```powershell
$env:RVP_RUN_FFMPEG_TESTS = "1"
dotnet test .\RaceVideoProcessor.sln -c Release
```

## Publish a self-contained Windows build

Put your chosen FFmpeg redistribution binaries in a directory containing `ffmpeg.exe` and `ffprobe.exe`, then run:

```powershell
.\scripts\publish-win-x64.ps1 -FfmpegDir "C:\tools\ffmpeg\bin"
```

The script publishes a self-contained `win-x64` application and copies the two tools into `tools\ffmpeg`. Review the license terms of the FFmpeg build you redistribute.

## Local state

By default the app stores:

```text
%LOCALAPPDATA%\RaceVideoProcessor\race-video-processor.db
%LOCALAPPDATA%\RaceVideoProcessor\race-video-processor.log
```

The API/mock provider remains the race-data source. SQLite stores local processing state and history.

## One-click Windows installer build

If you do not want to run the individual `dotnet` commands, use the installer builder included with the project.

On a Windows 10/11 x64 development PC, simply double-click:

```text
BUILD_INSTALLER.bat
```

It checks the build prerequisites, obtains FFmpeg if needed, runs the tests, publishes a self-contained Windows x64 application, bundles FFmpeg/FFprobe, and compiles a standard Windows installer.

The finished file is:

```text
artifacts\installer\RaceVideoProcessor-Setup-1.0.0.exe
```

You can then copy that single setup `.exe` to the target machine and install Race Video Processor normally. The target machine does **not** need the .NET runtime installed. See `installer\README-INSTALLER.md` for details.
