@echo off
rem Builds KeyboardLock.exe with the .NET Framework compiler bundled with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32icon:app.ico ^
  /out:KeyboardLock.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll KeyboardLock.cs
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Build OK: %~dp0KeyboardLock.exe
