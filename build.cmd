@echo off
rem Builds Photo Print Layout into a new folder builds\yyyyMMdd-HHmmss\ using the C# compiler that ships with Windows.
rem The build folder also gets Install.cmd / Uninstall.cmd, so you can install from there.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0.tools\build.ps1"
echo.
pause
