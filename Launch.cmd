@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1"
if errorlevel 1 goto failed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0dotnet\Start-CSharpLab.ps1"
if errorlevel 1 goto failed
powershell.exe -NoProfile -Command "$c=Get-Content -LiteralPath '%~dp0dotnet\runtime\bridge.json' -Raw|ConvertFrom-Json; Start-Process ('http://127.0.0.1:'+$c.dashboardPort+'/')"
exit /b 0
:failed
echo.
echo Startup failed. Read the message above and README.md.
pause
exit /b 1
