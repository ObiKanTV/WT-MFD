@echo off
setlocal
rem Builds the WT-MFD app (app\WT-MFD.exe) and puts WT-MFD on your desktop and in the Start menu.
rem Needs the .NET 8 SDK (or newer) and an internet connection, once. Run it again after big updates.
cd /d "%~dp0"

tasklist /fi "imagename eq WT-MFD.exe" 2>nul | find /i "WT-MFD.exe" >nul && (
    echo.
    echo  Close WT-MFD first, then run this again.
    echo.
    pause
    exit /b 1
)

rem keep your own settings: the build would put the default settings.json back
if exist "app\settings.json" copy /y "app\settings.json" "%TEMP%\wtmfd-settings.json" >nul

dotnet publish "source\src\WT-MFD.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o "%~dp0app"
set BUILD_ERROR=%errorlevel%

if exist "%TEMP%\wtmfd-settings.json" move /y "%TEMP%\wtmfd-settings.json" "app\settings.json" >nul
if not "%BUILD_ERROR%"=="0" (
    echo.
    echo  Build failed. Is the .NET SDK installed?  winget install Microsoft.DotNet.SDK.8
    echo  Is WT-MFD still running? Close it and try again.
    pause
    exit /b 1
)
rem the fresh build is newer than any waiting update
if exist "app\WT-MFD.dll.new" del "app\WT-MFD.dll.new"

rem shortcuts: desktop and Start menu
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = New-Object -ComObject WScript.Shell;" ^
  "foreach ($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {" ^
  "  $l = $s.CreateShortcut((Join-Path $d 'WT-MFD.lnk'));" ^
  "  $l.TargetPath = '%~dp0app\WT-MFD.exe'; $l.WorkingDirectory = '%~dp0app';" ^
  "  $l.IconLocation = '%~dp0app\wtmfd.ico'; $l.Description = 'War Thunder MFD'; $l.Save() }"

echo.
echo  Done. WT-MFD is on your desktop and in the Start menu.
echo.
pause
