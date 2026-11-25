@echo off
rem Installs or updates Photo Print Layout for the current user (no admin needed). Your settings are kept.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1" -Action Install
echo.
pause
