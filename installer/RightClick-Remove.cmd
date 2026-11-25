@echo off
rem Removes only the right-click menu entry and the Send To entry of Photo Print Layout.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1" -Action RemoveMenu
echo.
pause
