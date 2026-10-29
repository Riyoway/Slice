#define MyAppName "Slice"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Riyoway"
#define MyAppExeName "Slice.exe"

[Setup]
AppId={{B8F8AC2E-EB48-4FC7-9F2D-1B4C9D09E4E7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Slice
DefaultGroupName=Slice
DisableProgramGroupPage=yes
OutputDir=artifacts\installer
OutputBaseFilename=Slice-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=Assets\AppIcon.ico
UninstallDisplayIcon={app}\Assets\AppIcon.ico
PrivilegesRequired=lowest

[Files]
Source: "artifacts\portable\Slice-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Slice"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\Slice"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Slice"; Flags: nowait postinstall skipifsilent
