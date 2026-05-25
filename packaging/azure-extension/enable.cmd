@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0bin\Handler.ps1" -Operation Enable
exit /b %ERRORLEVEL%
