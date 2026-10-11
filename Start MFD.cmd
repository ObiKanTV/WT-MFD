@echo off
setlocal
cd /d "%~dp0"

rem An update delivered while WT-MFD was running waits as WT-MFD.dll.new; swap it in now.
if exist "app\WT-MFD.dll.new" move /y "app\WT-MFD.dll.new" "app\WT-MFD.dll" >nul

rem The WT-MFD app (made by "Build exe.cmd"): its own window, no console.
rem (A big WT-MFD.exe is the old all-in-one build from before the app window; skip that one.)
if exist "app\WT-MFD.exe" for %%F in ("app\WT-MFD.exe") do if %%~zF LSS 5000000 (
    start "" "app\WT-MFD.exe"
    goto :eof
)

rem Not built yet: run it through .NET. The MFD still opens in its own window; a console stays open beside it.
where dotnet >nul 2>nul || goto :nodotnet
start "WT-MFD" dotnet "app\WT-MFD.dll"
goto :eof

:nodotnet
echo.
echo  WT-MFD needs .NET 8 (or newer), and it isn't installed.
echo.
echo  Install the SDK once, then run "Build exe.cmd":
echo      winget install Microsoft.DotNet.SDK.8
echo.
pause
