@echo off
rem Builds SteamBypass.exe into the project root (next to steam-bypass.ps1).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /out:"%~dp0..\SteamBypass.exe" /win32manifest:"%~dp0app.manifest" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0SteamBypass.cs"
