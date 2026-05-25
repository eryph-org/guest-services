@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0bin\Handler.ps1" -Operation Disable
exit /b %ERRORLEVEL%
