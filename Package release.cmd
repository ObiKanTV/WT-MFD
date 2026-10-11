@echo off
setlocal
rem Builds a release zip in dist\: the app, the Stream Deck plugin, Start MFD.cmd, README, LICENSE and CHANGELOG.
rem Usage:  "Package release.cmd" 0.1.0-preview
rem Needs the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8). Users of the zip only need the .NET 8 runtime.
cd /d "%~dp0"

set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=0.1.0-preview"
set "OUT=dist\WT-MFD"
set "ZIP=dist\WT-MFD-v%VERSION%-win-x64.zip"

if exist dist rmdir /s /q dist
mkdir "%OUT%"

dotnet publish "source\src\WT-MFD.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:Version=%VERSION% -o "%OUT%\app"
if errorlevel 1 goto :fail

rem a release ships with default settings and no logs
if exist "%OUT%\app\logs" rmdir /s /q "%OUT%\app\logs"
if exist "%OUT%\app\window.json" del "%OUT%\app\window.json"

copy /y "Start MFD.cmd" "%OUT%\" >nul
copy /y README.md "%OUT%\" >nul
copy /y LICENSE "%OUT%\LICENSE.txt" >nul
copy /y CHANGELOG.md "%OUT%\" >nul
robocopy streamdeck "%OUT%\streamdeck" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :fail

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :fail

echo.
echo  Done: %ZIP%
echo.
exit /b 0

:fail
echo.
echo  Packaging failed. Is the .NET 8 SDK installed?  winget install Microsoft.DotNet.SDK.8
echo.
exit /b 1
