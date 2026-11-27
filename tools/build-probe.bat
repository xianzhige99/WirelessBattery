@echo off
setlocal
rem One-click wrapper. The real work is in build-probe.ps1 (single source of truth).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-probe.ps1"
if errorlevel 1 (
  echo.
  echo [FAILED] Build failed. Copy the error lines above and send them back.
  pause
  exit /b 1
)
echo.
echo Next: close WirelessBattery first ^(it holds the HID handle^), then run
echo     "%~dp0..\bin\GpwBatteryProbe.exe" --watch
echo.
pause
