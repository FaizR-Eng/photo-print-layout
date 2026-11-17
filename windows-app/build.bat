@echo off
rem Builds PhotoPrintLayout.exe using the C# compiler included with Windows (.NET Framework 4).
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:PhotoPrintLayout.exe /win32icon:app.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xaml.dll ^
  /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  PhotoPrintLayout.cs
if errorlevel 1 (echo Build failed & pause) else echo Built PhotoPrintLayout.exe
