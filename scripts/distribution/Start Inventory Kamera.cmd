@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0support\Start-InventoryKamera.ps1"
if errorlevel 1 pause
