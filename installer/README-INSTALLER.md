# Building the Windows installer

For the simplest build on a Windows 10/11 x64 development PC:

1. Extract the source folder.
2. Double-click `BUILD_INSTALLER.bat`.
3. Wait for the tests, publish, and installer compilation to finish.
4. The finished installer will be in:

   `artifacts\installer\RaceVideoProcessor-Setup-1.0.0.exe`

The builder creates a **self-contained .NET 8 x64 application**, so the target race-processing computer does not need to have .NET installed.

The builder also bundles `ffmpeg.exe` and `ffprobe.exe` into the application under `tools\ffmpeg`. If they are not already present in `vendor\ffmpeg` or on `PATH`, the build script downloads an FFmpeg essentials build for bundling.

## What the installer does

- Installs into `Program Files\Race Video Processor` by default.
- Creates a Start Menu shortcut.
- Optionally creates a Desktop shortcut.
- Registers a standard Windows uninstaller.
- Keeps application state under `%LOCALAPPDATA%\RaceVideoProcessor`, so uninstalling/upgrading the program does not silently delete processing history.
- Bundles FFmpeg/FFprobe so normal video processing does not require internet access.

## Advanced build

From PowerShell:

```powershell
.\scripts\build-installer.ps1 -FfmpegDir "C:\ffmpeg\bin"
```

Skip tests when iterating locally:

```powershell
.\scripts\build-installer.ps1 -SkipTests
```

Prevent automatic FFmpeg download:

```powershell
.\scripts\build-installer.ps1 -NoFfmpegDownload
```

## FFmpeg licensing

FFmpeg is a separate open-source project. Before distributing an installer outside your own/internal environment, review the license of the exact FFmpeg binary build being bundled and comply with its applicable LGPL/GPL requirements. The builder writes the binary source URL into the bundled FFmpeg directory.
