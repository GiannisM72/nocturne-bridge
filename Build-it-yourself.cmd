@echo off
rem Builds NocturneBridge.exe from NocturneBridge.cs with the C# compiler that ships with Windows (.NET Framework 4).
rem Use this if you prefer not to run a downloaded .exe.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0NocturneBridge.exe" /win32icon:"%~dp0nocturne.ico" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0NocturneBridge.cs"
if errorlevel 1 (echo Build failed.) else (echo Built: %~dp0NocturneBridge.exe)
pause
