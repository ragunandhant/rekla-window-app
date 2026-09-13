# Validation performed in the generation environment

## Completed checks

- All `.xaml` and `.csproj` files were parsed as XML successfully.
- All C# source files passed a delimiter-balance scan that ignores comments and string/character literals.
- The FFmpeg overlay/filter approach was smoke-tested with the environment's FFmpeg 7.1.5 on a generated 1280×720 / 30 fps video with audio.
- The smoke test exercised:
  - bottom panel `drawbox` layers
  - left / center / right `drawtext`
  - final-time `enable=between(...)`
  - `libx264` video encode
  - audio stream copy
  - `-progress pipe:1`
  - FFprobe validation
- Smoke-test output retained 1280×720 resolution, a video stream, an audio stream and 2.000 s duration. FFmpeg reported real progress and completed successfully.

## What could not be executed here

The generation container does not contain the .NET SDK and is Linux-based. Therefore the WPF solution itself could not be compiled or launched in this environment, and the xUnit suite could not be executed here.

Run the following on a Windows development machine with the .NET 8 SDK:

```powershell
dotnet restore .\RaceVideoProcessor.sln
dotnet build .\RaceVideoProcessor.sln -c Release
dotnet test .\RaceVideoProcessor.sln -c Release
```

For the opt-in test that actually invokes FFmpeg:

```powershell
$env:RVP_RUN_FFMPEG_TESTS = "1"
dotnet test .\RaceVideoProcessor.sln -c Release
```

The application is intentionally a Windows/WPF target; a Windows build/run is the final required verification step before deployment.
