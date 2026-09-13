@echo off
setlocal
cd /d "%~dp0"
echo.
echo =====================================================
echo   Race Video Processor - Installer Builder
echo =====================================================
echo.
echo This will:
echo   1. Check/install .NET 8 SDK and Inno Setup if needed
echo   2. Download FFmpeg if it is not already available
echo   3. Run tests
echo   4. Build a self-contained Windows application
echo   5. Create RaceVideoProcessor-Setup-1.0.0.exe
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-installer.ps1"
if errorlevel 1 (
  echo.
  echo BUILD FAILED. See the error above.
  pause
  exit /b 1
)
echo.
echo Installer created under:
echo   artifacts\installer\
echo.
pause
