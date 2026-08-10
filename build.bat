@echo off
REM EQ Avatar - Phase 0 Spike release build
REM (run.bat auto-installs the SDK; use that first if dotnet is missing)
if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
  set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
)
dotnet build -c Release
echo.
echo Output: bin\Release\net8.0-windows\EQAvatar.Spike.exe
pause
