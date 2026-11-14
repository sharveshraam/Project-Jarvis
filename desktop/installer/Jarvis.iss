; JARVIS Desktop — per-user Inno Setup installer (no admin required).
; Build: ISCC.exe Jarvis.iss  (run by .github/workflows/jarvis-desktop.yml)
#define MyAppName "JARVIS"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Project JARVIS"
#define MyAppExeName "JARVIS.exe"

[Setup]
AppId={{8F3A2C1E-7B4D-4E9A-9C2F-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\JARVIS
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish
OutputBaseFilename=JARVIS-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "startupicon"; Description: "Start JARVIS with Windows (system tray)"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch JARVIS now"; Flags: nowait postinstall shellexec skipifsilent

[UninstallRun]
; Data (settings, database, API key) is kept on uninstall — remove manually if desired.
