@echo off
setlocal
title EQ Avatar - Phase 0 Spike (launcher)
cd /d "%~dp0"

set "EXE=%~dp0publish\EQAvatar.Spike.exe"

rem ---- If we've already built the standalone exe, just launch it and get out of the way ----
if exist "%EXE%" (
  start "" "%EXE%"
  exit /b 0
)

echo First run: building a standalone EQ Avatar.exe (no runtime needed afterwards).
echo This window closes by itself once the app opens.
echo.

rem ---- Make sure a .NET SDK is reachable (installs one if not) ----
set "SDK_OK="
where dotnet >nul 2>&1 && ( dotnet --list-sdks 2>nul | findstr /b /c:"8." /c:"9." /c:"10." >nul && set "SDK_OK=1" )
if not defined SDK_OK if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
  set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
  dotnet --list-sdks 2>nul | findstr /b /c:"8." /c:"9." /c:"10." >nul && set "SDK_OK=1"
)
if not defined SDK_OK (
  where winget >nul 2>&1 && winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-source-agreements --accept-package-agreements
  if not defined SDK_OK if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"
)
where dotnet >nul 2>&1 || (
  echo.
  echo Could not find or install the .NET SDK automatically.
  echo Install it from https://dotnet.microsoft.com/download/dotnet/8.0 then re-run.
  pause & exit /b 1
)

rem ---- Publish a single self-contained exe (bundles the runtime; zero console) ----
dotnet publish "%~dp0EQAvatar.Spike.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish"
if errorlevel 1 (
  echo.
  echo Build failed. If it's a compile error, send the text above to Claude.
  pause & exit /b 1
)

if exist "%EXE%" (
  echo.
  echo Done. Launching... From now on you can just double-click:
  echo   %EXE%
  start "" "%EXE%"
)
exit /b 0
