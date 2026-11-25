@echo off
rem Removes Photo Print Layout, its shortcuts, right-click menu and Send To entry. Your settings and layouts are kept.
rem Runs from a temporary copy, because the program folder (where this file may live) is removed.
set "PPLTMP=%TEMP%\PhotoPrintLayout-uninstall"
if /i "%~dp0"=="%PPLTMP%\" goto run
if not exist "%PPLTMP%" mkdir "%PPLTMP%"
copy /y "%~dp0installer.ps1" "%PPLTMP%\installer.ps1" >nul
copy /y "%~f0" "%PPLTMP%\Uninstall.cmd" >nul
"%PPLTMP%\Uninstall.cmd"
exit /b
:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer.ps1" -Action Uninstall
echo.
pause
