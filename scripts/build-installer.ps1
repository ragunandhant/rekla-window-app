param(
    [string]$FfmpegDir = "",
    [switch]$SkipTests,
    [switch]$NoFfmpegDownload,
    [switch]$NoFontDownload
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "This installer builder must be run on Windows."
}

$root = (Resolve-Path "$PSScriptRoot\..").Path
$publishDir = Join-Path $root "artifacts\win-x64"
$installerDir = Join-Path $root "artifacts\installer"
$vendorFfmpegDir = Join-Path $root "vendor\ffmpeg"
$vendorFontDir = Join-Path $root "vendor\fonts"
$appProject = Join-Path $root "src\RaceVideoProcessor.App\RaceVideoProcessor.App.csproj"
$solution = Join-Path $root "RaceVideoProcessor.sln"
$issFile = Join-Path $root "installer\RaceVideoProcessor.iss"

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Find-Iscc {
    # Inno Setup can be installed machine-wide (Program Files) or per-user
    # (LocalAppData), depending on winget/UAC choices. Check all common paths.
    $candidates = New-Object System.Collections.Generic.List[string]

    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"))
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"))
    }
    if ($env:LOCALAPPDATA) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"))
        $candidates.Add((Join-Path $env:LOCALAPPDATA "Inno Setup 6\ISCC.exe"))
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ($candidate -and (Test-Path $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }

    # If ISCC is already on PATH, use it.
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # Finally inspect Inno Setup's uninstall registry entries. This catches
    # custom install locations without doing a slow recursive disk search.
    $uninstallKeys = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
    )

    foreach ($key in $uninstallKeys) {
        if (-not (Test-Path $key)) { continue }
        $props = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if (-not $props) { continue }

        if ($props.InstallLocation) {
            $candidate = Join-Path $props.InstallLocation "ISCC.exe"
            if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
        }

        if ($props.DisplayIcon) {
            $iconPath = ($props.DisplayIcon -replace ',\d+$', '').Trim('"')
            $installDir = Split-Path $iconPath -Parent
            if ($installDir) {
                $candidate = Join-Path $installDir "ISCC.exe"
                if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
            }
        }
    }

    return $null
}

function Ensure-WingetPackage([string]$Id, [string]$DisplayName) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw "$DisplayName is required, and winget was not found. Install $DisplayName manually, then rerun BUILD_INSTALLER.bat."
    }
    Write-Host "$DisplayName not found. Installing it with winget..." -ForegroundColor Yellow
    & winget install --id $Id --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget could not install $DisplayName (exit code $LASTEXITCODE)."
    }
}

function Ensure-Dotnet8 {
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Ensure-WingetPackage "Microsoft.DotNet.SDK.8" ".NET 8 SDK"
        $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if (-not $dotnet) {
            $candidate = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
            if (Test-Path $candidate) { $dotnet = Get-Item $candidate }
        }
    }
    if (-not $dotnet) { throw ".NET SDK was installed but dotnet.exe could not be located. Open a new terminal and run BUILD_INSTALLER.bat again." }

    $major = (& $dotnet.Source --version).Split('.')[0]
    if ([int]$major -lt 8) {
        Ensure-WingetPackage "Microsoft.DotNet.SDK.8" ".NET 8 SDK"
    }
    return $dotnet.Source
}

function Ensure-InnoSetup {
    $iscc = Find-Iscc
    if (-not $iscc) {
        Ensure-WingetPackage "JRSoftware.InnoSetup" "Inno Setup 6"

        # winget may complete a per-user install without refreshing the current
        # PowerShell process environment. Find-Iscc checks the install folders
        # directly, so no terminal restart should be required.
        $iscc = Find-Iscc
    }

    if (-not $iscc) {
        throw @"
Inno Setup 6 appears to have been installed, but ISCC.exe could not be found.

Try one of these paths in File Explorer:
  $env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe
  $env:ProgramFiles\Inno Setup 6\ISCC.exe
  ${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe

If Inno Setup is present, close this window and run BUILD_INSTALLER.bat again.
"@
    }

    Write-Host "Using Inno Setup compiler: $iscc" -ForegroundColor DarkGray
    return $iscc
}

function Find-FfmpegPair([string]$ExplicitDir) {
    $dirs = New-Object System.Collections.Generic.List[string]
    if ($ExplicitDir) { $dirs.Add($ExplicitDir) }
    $dirs.Add($vendorFfmpegDir)

    $ffmpegCmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    $ffprobeCmd = Get-Command ffprobe.exe -ErrorAction SilentlyContinue
    if ($ffmpegCmd -and $ffprobeCmd) {
        $ffmpegParent = Split-Path $ffmpegCmd.Source -Parent
        $ffprobeParent = Split-Path $ffprobeCmd.Source -Parent
        if ($ffmpegParent -eq $ffprobeParent) { $dirs.Add($ffmpegParent) }
    }

    foreach ($dir in $dirs | Select-Object -Unique) {
        if (-not $dir) { continue }
        $ffmpeg = Join-Path $dir "ffmpeg.exe"
        $ffprobe = Join-Path $dir "ffprobe.exe"
        if ((Test-Path $ffmpeg) -and (Test-Path $ffprobe)) {
            return (Resolve-Path $dir).Path
        }
    }
    return $null
}

function Download-Ffmpeg {
    Write-Step "Downloading FFmpeg essentials for bundling"
    New-Item -ItemType Directory -Path $vendorFfmpegDir -Force | Out-Null
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("rvp-ffmpeg-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null
    $zip = Join-Path $tmp "ffmpeg.zip"
    $url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath $tmp -Force
        $ffmpeg = Get-ChildItem $tmp -Filter ffmpeg.exe -File -Recurse | Select-Object -First 1
        $ffprobe = Get-ChildItem $tmp -Filter ffprobe.exe -File -Recurse | Select-Object -First 1
        if (-not $ffmpeg -or -not $ffprobe) { throw "Downloaded archive did not contain ffmpeg.exe and ffprobe.exe." }
        Copy-Item $ffmpeg.FullName (Join-Path $vendorFfmpegDir "ffmpeg.exe") -Force
        Copy-Item $ffprobe.FullName (Join-Path $vendorFfmpegDir "ffprobe.exe") -Force

        $license = Get-ChildItem $tmp -Include COPYING*,LICENSE* -File -Recurse | Select-Object -First 1
        if ($license) { Copy-Item $license.FullName (Join-Path $vendorFfmpegDir $license.Name) -Force }
        @"
FFmpeg binaries downloaded from:
$url

FFmpeg is a separate open-source project. See https://ffmpeg.org/legal.html and the accompanying license file (when present) for licensing information.
"@ | Set-Content (Join-Path $vendorFfmpegDir "FFMPEG-SOURCE.txt") -Encoding UTF8
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
    return $vendorFfmpegDir
}

function Ensure-TamilFont {
    # The race data is Tamil. Nirmala UI ships with Windows and covers Tamil, so
    # the application always has a fallback, but bundling Noto Sans Tamil (SIL
    # Open Font License) means the rendered video does not depend on what happens
    # to be installed on the machine.
    $target = Join-Path $vendorFontDir "NotoSansTamil-Regular.ttf"
    if (Test-Path $target) { return $vendorFontDir }
    if ($NoFontDownload) {
        Write-Host "Skipping Tamil font download; the application will fall back to a Tamil-capable system font." -ForegroundColor Yellow
        return $null
    }

    Write-Step "Downloading Noto Sans Tamil for bundling"
    New-Item -ItemType Directory -Path $vendorFontDir -Force | Out-Null
    $url = "https://github.com/google/fonts/raw/main/ofl/notosanstamil/NotoSansTamil%5Bwdth%2Cwght%5D.ttf"
    try {
        Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
        @"
Noto Sans Tamil downloaded from:
$url

Licensed under the SIL Open Font License 1.1. See https://scripts.sil.org/OFL
"@ | Set-Content (Join-Path $vendorFontDir "FONT-SOURCE.txt") -Encoding UTF8
        return $vendorFontDir
    }
    catch {
        Remove-Item $target -Force -ErrorAction SilentlyContinue
        Write-Host "Could not download the Tamil font ($($_.Exception.Message)). The application will fall back to a Tamil-capable system font such as Nirmala UI." -ForegroundColor Yellow
        return $null
    }
}

Write-Step "Checking build tools"
$dotnet = Ensure-Dotnet8
$iscc = Ensure-InnoSetup

$resolvedFfmpegDir = Find-FfmpegPair $FfmpegDir
if (-not $resolvedFfmpegDir) {
    if ($NoFfmpegDownload) {
        throw "FFmpeg was not found. Put ffmpeg.exe and ffprobe.exe in vendor\ffmpeg, add them to PATH, or rerun without -NoFfmpegDownload."
    }
    $resolvedFfmpegDir = Download-Ffmpeg
}

Write-Step "Cleaning previous installer output"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $installerDir) { Remove-Item $installerDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

Write-Step "Restoring packages"
& $dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

if (-not $SkipTests) {
    Write-Step "Running unit tests"
    & $dotnet test $solution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Tests failed. Installer was not created." }
}

Write-Step "Publishing self-contained Windows application"
& $dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Step "Bundling FFmpeg and FFprobe"
$toolDir = Join-Path $publishDir "tools\ffmpeg"
New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
Copy-Item (Join-Path $resolvedFfmpegDir "ffmpeg.exe") $toolDir -Force
Copy-Item (Join-Path $resolvedFfmpegDir "ffprobe.exe") $toolDir -Force
Get-ChildItem $resolvedFfmpegDir -Include COPYING*,LICENSE*,FFMPEG-SOURCE.txt -File -ErrorAction SilentlyContinue | Copy-Item -Destination $toolDir -Force

$appExe = Join-Path $publishDir "RaceVideoProcessor.exe"
if (-not (Test-Path $appExe)) { throw "Publish completed but RaceVideoProcessor.exe was not created." }
if (-not (Test-Path (Join-Path $toolDir "ffmpeg.exe"))) { throw "ffmpeg.exe was not bundled." }
if (-not (Test-Path (Join-Path $toolDir "ffprobe.exe"))) { throw "ffprobe.exe was not bundled." }

Write-Step "Bundling the Tamil scoreboard font"
$resolvedFontDir = Ensure-TamilFont
if ($resolvedFontDir) {
    $fontTarget = Join-Path $publishDir "tools\fonts"
    New-Item -ItemType Directory -Path $fontTarget -Force | Out-Null
    Get-ChildItem $resolvedFontDir -Include *.ttf,*.otf,FONT-SOURCE.txt,OFL* -File -ErrorAction SilentlyContinue |
        Copy-Item -Destination $fontTarget -Force
    # The resolver looks for this exact name first.
    $variable = Get-ChildItem $fontTarget -Filter "NotoSansTamil*.ttf" -File | Select-Object -First 1
    if ($variable -and $variable.Name -ne "NotoSansTamil-Regular.ttf") {
        Copy-Item $variable.FullName (Join-Path $fontTarget "NotoSansTamil-Regular.ttf") -Force
    }
}

Write-Step "Compiling Windows installer"
& $iscc $issFile
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$installer = Get-ChildItem $installerDir -Filter "RaceVideoProcessor-Setup-*.exe" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $installer) { throw "Installer compiler succeeded but no setup executable was found." }

Write-Host "`nSUCCESS" -ForegroundColor Green
Write-Host "Installer: $($installer.FullName)" -ForegroundColor Green
Write-Host "`nYou can copy this single .exe to the target Windows computer and run it." -ForegroundColor White
