@echo off
setlocal
rem Builds a release in dist\ on this PC, like the GitHub release workflow does:
rem   dist\WT-MFD-v<version>-portable-win-x64.zip   and, if Inno Setup 6 is installed,
rem   dist\WT-MFD-Setup-v<version>-win-x64.exe      (winget install JRSoftware.InnoSetup)
rem Usage:  "Package release.cmd" 0.1.0-preview
rem Needs the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8). The result is self-contained: users need no .NET.
cd /d "%~dp0"

set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=0.1.0-preview"
set "OUT=dist\WT-MFD"
set "ZIP=dist\WT-MFD-v%VERSION%-portable-win-x64.zip"

if exist dist rmdir /s /q dist
mkdir "%OUT%"

dotnet publish "source\src\WT-MFD.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=%VERSION% -o "%OUT%\app"
if errorlevel 1 goto :fail

copy /y "Start MFD.cmd" "%OUT%\" >nul
copy /y README.md "%OUT%\" >nul
copy /y CHANGELOG.md "%OUT%\" >nul
copy /y installer\DISCLAIMER.txt "%OUT%\" >nul
copy /y LICENSE "%OUT%\LICENSE.txt" >nul
robocopy streamdeck "%OUT%\streamdeck" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto :fail

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :fail
echo  Portable zip: %ZIP%

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
    echo  Inno Setup 6 not found, so no installer. winget install JRSoftware.InnoSetup
    goto :done
)
"%ISCC%" /Q /DAppVersion=%VERSION% installer\WT-MFD.iss
if errorlevel 1 goto :fail
echo  Installer:    dist\WT-MFD-Setup-v%VERSION%-win-x64.exe

:done
echo.
exit /b 0

:fail
echo.
echo  Packaging failed. Is the .NET 8 SDK installed?  winget install Microsoft.DotNet.SDK.8
echo.
exit /b 1
