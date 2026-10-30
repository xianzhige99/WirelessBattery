@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "Unblock-File -LiteralPath '%~dp0bin\WirelessBattery.exe' -ErrorAction SilentlyContinue"
start "" "%~dp0bin\WirelessBattery.exe"
