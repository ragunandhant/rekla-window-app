param(
    [Parameter(Mandatory=$false)]
    [string]$FfmpegDir = "",

    [Parameter(Mandatory=$false)]
    [string]$OutputDir = "$PSScriptRoot\..\artifacts\win-x64"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
$appProject = Join-Path $root "src\RaceVideoProcessor.App\RaceVideoProcessor.App.csproj"

Write-Host "Publishing Race Video Processor..." -ForegroundColor Cyan
dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -o $OutputDir

if ($FfmpegDir) {
    $ffmpeg = Join-Path $FfmpegDir "ffmpeg.exe"
    $ffprobe = Join-Path $FfmpegDir "ffprobe.exe"
    if (!(Test-Path $ffmpeg) -or !(Test-Path $ffprobe)) {
        throw "FfmpegDir must contain ffmpeg.exe and ffprobe.exe"
    }
    $toolDir = Join-Path $OutputDir "tools\ffmpeg"
    New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
    Copy-Item $ffmpeg $toolDir -Force
    Copy-Item $ffprobe $toolDir -Force
    Write-Host "Bundled FFmpeg tools into $toolDir" -ForegroundColor Green
}

Write-Host "Publish complete: $OutputDir" -ForegroundColor Green
