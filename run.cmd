@echo off
setlocal
cd /d "%~dp0"
"C:\Program Files\dotnet\dotnet.exe" run --project "src\GameTuner.App\GameTuner.App.csproj" --no-restore
