; WT-MFD installer (Inno Setup 6.3 or newer).
;
; Built by .github/workflows/release.yml for every release. To build it yourself, publish the app first
; ("Package release.cmd" does both), or by hand:
;   dotnet publish source\src\WT-MFD.csproj -c Release -r win-x64 --self-contained true -o dist\WT-MFD\app
;   ISCC.exe /DAppVersion=0.1.0-preview installer\WT-MFD.iss
;
; The installer is per-user (no admin rights): WT-MFD writes its settings, window position and match logs
; next to WT-MFD.exe, so it must live in a folder the user can write to (%LOCALAPPDATA%\Programs\WT-MFD).

#ifndef AppVersion
  #define AppVersion "0.1.0-preview"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\WT-MFD\app"
#endif
; Windows file versions are numbers only: 0.1.0-preview -> 0.1.0
#define AppNumVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)

[Setup]
; Never change AppId: Windows uses it to recognise an installed WT-MFD when upgrading
AppId={{B477A213-E853-4072-BAEE-42056B31F5FA}
AppName=WT-MFD
AppVersion={#AppVersion}
AppVerName=WT-MFD {#AppVersion}
AppPublisher=ObiKan
AppPublisherURL=https://github.com/ObiKanTV/WT-MFD
AppSupportURL=https://github.com/ObiKanTV/WT-MFD/issues
AppUpdatesURL=https://github.com/ObiKanTV/WT-MFD/releases
AppComments=Unofficial second-screen MFD for War Thunder. Not affiliated with Gaijin Entertainment.
VersionInfoVersion={#AppNumVersion}
VersionInfoProductVersion={#AppNumVersion}
VersionInfoDescription=WT-MFD Setup
DefaultDirName={autopf}\WT-MFD
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
InfoBeforeFile=DISCLAIMER.txt
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=WT-MFD-Setup-v{#AppVersion}-win-x64
SetupIconFile=..\source\src\wtmfd.ico
UninstallDisplayIcon={app}\WT-MFD.exe
UninstallDisplayName=WT-MFD
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; an open WT-MFD is closed before its files are replaced
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "streamdeck"; Description: "Install the WT-MFD Stream Deck plugin"; GroupDescription: "Stream Deck:"; Check: StreamDeckInstalled

[Files]
; the app (self-contained: no .NET install needed)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "settings.json,logs,window.json,*.dll.new"; Flags: ignoreversion recursesubdirs createallsubdirs
; default settings only on a fresh install: an upgrade keeps yours
Source: "{#SourceDir}\settings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "DISCLAIMER.txt"; DestDir: "{app}"; Flags: ignoreversion
; Stream Deck: the plugin goes straight into Stream Deck's plugin folder; the folder icons stay with the app
Source: "..\streamdeck\com.kai.wtmfd.sdPlugin\*"; DestDir: "{userappdata}\Elgato\StreamDeck\Plugins\com.kai.wtmfd.sdPlugin"; Flags: ignoreversion recursesubdirs createallsubdirs; Tasks: streamdeck
Source: "..\streamdeck\icons\*"; DestDir: "{app}\streamdeck-icons"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WT-MFD"; Filename: "{app}\WT-MFD.exe"; WorkingDir: "{app}"
Name: "{autoprograms}\WT-MFD settings"; Filename: "{win}\notepad.exe"; Parameters: """{app}\settings.json"""; IconFilename: "{app}\WT-MFD.exe"
Name: "{autodesktop}\WT-MFD"; Filename: "{app}\WT-MFD.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WT-MFD.exe"; Description: "{cm:LaunchProgram,WT-MFD}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
; Stream Deck only loads new plugin code when it starts
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -WindowStyle Hidden -Command ""Stop-Process -Name StreamDeck -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; $e = Join-Path $env:ProgramW6432 'Elgato\StreamDeck\StreamDeck.exe'; if (Test-Path $e) {{ Start-Process $e }"""; Description: "Restart Stream Deck to load the plugin"; Flags: postinstall runhidden skipifsilent; Tasks: streamdeck

[UninstallDelete]
; files WT-MFD made itself: match logs (other players' names and chat), window position, settings, pending update
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\window.json"
Type: files; Name: "{app}\settings.json"
Type: files; Name: "{app}\WT-MFD.dll.new"
Type: dirifempty; Name: "{app}"

[Code]
function StreamDeckInstalled: Boolean;
begin
  Result := DirExists(ExpandConstant('{userappdata}\Elgato\StreamDeck'));
end;

{ WT-MFD draws its window with the Edge WebView2 runtime. It ships with Windows 11 and up-to-date Windows 10. }
function WebView2Installed: Boolean;
var
  V: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not WebView2Installed) then
    MsgBox('WT-MFD needs the Microsoft Edge WebView2 Runtime to draw its window, and it was not found.' + #13#10#13#10 +
      'Install it from https://developer.microsoft.com/microsoft-edge/webview2/ or run:' + #13#10 +
      '    winget install Microsoft.EdgeWebView2Runtime', mbInformation, MB_OK);
end;
