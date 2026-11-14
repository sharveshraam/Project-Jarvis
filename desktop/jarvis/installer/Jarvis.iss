; JARVIS Desktop installer (Inno Setup 6.3 or later).
;
; Build:  iscc installer\Jarvis.iss /DAppVersion=1.0.0 /DSourceDir=..\publish
; Input:  the self-contained publish folder produced by
;         dotnet publish src\Jarvis.Desktop\Jarvis.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish
; Output: installer\Output\JARVIS-Setup-<version>.exe
;
; Installs per user (no administrator rights), under %LOCALAPPDATA%\Programs\JARVIS.
; User data lives separately in %LOCALAPPDATA%\JARVIS and is kept on upgrade.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "JARVIS"
#define AppExe "Jarvis.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{6B0C1F4E-3A52-4D8B-9E17-2C5D8A7F41B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Project JARVIS
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=JARVIS-Setup-{#AppVersion}
SetupIconFile=..\assets\jarvis.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Matches the single-instance mutex in App.xaml.cs, so Setup asks for JARVIS to be closed before replacing files.
AppMutex=ProjectJarvis.Desktop.Instance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; Starting with Windows is owned by the application, not by Setup: JARVIS keeps the per-user Run entry in step
; with the start-up switch under Settings > General (on by default) every time it starts. The uninstaller removes it.

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { The start-up entry is created by the application itself (StartupManager); remove it with the program. }
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#AppName}');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\JARVIS');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Also delete your JARVIS data?' + #13#10 + #13#10 +
                'This removes conversations, reminders, tasks, memory, settings, logs and the stored Gemini API key from:' + #13#10 +
                DataDir + #13#10 + #13#10 +
                'Choose No to keep them for a later reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
