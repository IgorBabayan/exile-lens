; Inno Setup script for Exile Lens (per-user install, no admin rights required).
; Invoked by .github/workflows/release.yml:
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=...\publish\win-x64 /DOutputDir=...\dist /DRid=win-x64 installer.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Rid
  #define Rid "win-x64"
#endif

[Setup]
AppId={{6E1F2B7C-3A4D-4E8B-9C21-5D7A0E9F4B13}
AppName=Exile Lens
AppVersion={#AppVersion}
AppPublisher=Exile Lens contributors
AppPublisherURL=https://github.com/IgorBabayan/exile-lens
DefaultDirName={localappdata}\Programs\ExileLens
DefaultGroupName=Exile Lens
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=ExileLens-{#AppVersion}-{#Rid}-setup
SetupIconFile=..\..\src\ExileLens.App\Assets\icon.ico
UninstallDisplayIcon={app}\ExileLens.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible arm64
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Exile Lens"; Filename: "{app}\ExileLens.exe"
Name: "{autodesktop}\Exile Lens"; Filename: "{app}\ExileLens.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ExileLens.exe"; Description: "{cm:LaunchProgram,Exile Lens}"; Flags: nowait postinstall skipifsilent

; Settings, profiles and history live in %APPDATA%\ExileLens and are intentionally left untouched on upgrade/uninstall.
