@echo off
cd /d "%~dp0"
taskkill /f /im WirelessBattery.exe >nul 2>&1
taskkill /f /im 无线设备电量.exe >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%~dp0bin\WirelessBattery.exe' -ErrorAction SilentlyContinue; Start-Process -FilePath '%~dp0bin\WirelessBattery.exe' -ArgumentList '--tray' -Verb RunAs"
