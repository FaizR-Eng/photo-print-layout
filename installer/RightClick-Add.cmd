@echo off
rem Adds only "Open in Photo Print Layout" to the right-click menu of photos, plus a Send To entry (current user, no admin).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1" -Action AddMenu
echo.
pause
