@echo off
rem Installs the WT-MFD Stream Deck plugin: copies it into Stream Deck's plugin folder and restarts Stream Deck.
set "SRC=%~dp0com.kai.wtmfd.sdPlugin"
set "DEST=%APPDATA%\Elgato\StreamDeck\Plugins\com.kai.wtmfd.sdPlugin"

echo Closing Stream Deck...
taskkill /f /im StreamDeck.exe >nul 2>nul
timeout /t 2 /nobreak >nul

robocopy "%SRC%" "%DEST%" /MIR /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo Copy failed. Is the folder "%SRC%" there?
    pause
    exit /b 1
)

echo Starting Stream Deck...
if exist "%ProgramFiles%\Elgato\StreamDeck\StreamDeck.exe" (
    start "" "%ProgramFiles%\Elgato\StreamDeck\StreamDeck.exe"
) else (
    echo Could not find StreamDeck.exe - start the Stream Deck app yourself.
)
echo.
echo Done. In Stream Deck, find "WT-MFD" in the action list on the right and drag
echo "MFD Button" onto all 15 keys. Each key sets itself up from its position.
echo.
pause
