; HashRelay for Windows — per-user installer (no elevation).
; Build with build.ps1, which publishes the agent and CLI and passes the version and source folders.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AgentDir
  #define AgentDir "..\artifacts\agent"
#endif
#ifndef CliDir
  #define CliDir "..\artifacts\cli"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "HashRelay"
#define AppPublisher "Avlon Technologies"
#define AppUrl "https://hashrelay.dev.avlon.ca"
#define AgentExe "HashRelay.Agent.exe"
#define CliExe "hashrelay.exe"
#define Aumid "Avlon.HashRelay"

[Setup]
AppId={{6E2B6E0C-4D1C-4F3B-9B8F-2C5E1D7A9F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/docs
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
OutputDir={#OutputDir}
OutputBaseFilename=HashRelay-Setup
SetupIconFile=..\assets\hashrelay.ico
UninstallDisplayIcon={app}\{#AgentExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
WizardSizePercent=110
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
ChangesEnvironment=yes
ShowLanguageDialog=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install the {#AppName} agent and the hashrelay command line on your computer, for your user account only.%n%nThe agent shows hashtag notifications in Windows Notification Center. After installing, it will ask you to pair this device with your workspace.
FinishedLabelNoIcons=Setup has finished installing {#AppName}.%n%nOpen a new terminal and run  hashrelay --help  to get started.

[Tasks]
Name: "startup"; Description: "Start the {#AppName} agent when I sign in to Windows"; GroupDescription: "Startup:"
Name: "path"; Description: "Add hashrelay to my PATH (for the command line)"; GroupDescription: "Command line:"

[Files]
Source: "{#AgentDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CliDir}\{#CliExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\hashrelay.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\hashrelay.png"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AgentExe}"; IconFilename: "{app}\hashrelay.ico"; AppUserModelID: "{#Aumid}"; Comment: "Hashtag notifications for your desktop"
Name: "{userprograms}\{#AppName} Dashboard"; Filename: "{#AppUrl}/dashboard"; IconFilename: "{app}\hashrelay.ico"

[InstallDelete]
; The command line was called hash-relay.exe before 0.2; remove it so the old name does not linger on PATH.
Type: files; Name: "{app}\hash-relay.exe"

[Registry]
; Start with Windows (per user). The agent also exposes a toggle for this in its tray menu.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AgentExe}"" --silent"; Flags: uninsdeletevalue; Tasks: startup
; Notification identity: display name and icon shown by Windows for our toasts.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#Aumid}"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#Aumid}"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\hashrelay.png"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#Aumid}"; ValueType: string; ValueName: "IconBackgroundColor"; ValueData: "FF7C6CFF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#Aumid}"; ValueType: dword; ValueName: "ShowInSettings"; ValueData: 1; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AgentExe}"; Description: "Start {#AppName} and pair this device"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AgentExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitAgent"
Filename: "{app}\{#AgentExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterToasts"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  EnvKey = 'Environment';

procedure StopAgent;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AgentExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function NeedsAddPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Path) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

procedure AddToUserPath(Dir: string);
var
  Path: string;
begin
  if not NeedsAddPath(Dir) then exit;
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Path) then Path := '';
  if (Path <> '') and (Path[Length(Path)] <> ';') then Path := Path + ';';
  Path := Path + Dir;
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Path);
end;

procedure RemoveFromUserPath(Dir: string);
var
  Path, Upper: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Path) then exit;
  Upper := ';' + Uppercase(Path) + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Upper);
  if P = 0 then exit;
  Path := ';' + Path + ';';
  Delete(Path, P, Length(Dir) + 1);
  { trim the sentinels we added }
  if (Length(Path) > 0) and (Path[1] = ';') then Delete(Path, 1, 1);
  if (Length(Path) > 0) and (Path[Length(Path)] = ';') then Delete(Path, Length(Path), 1);
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Path);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAgent;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('path') then AddToUserPath(ExpandConstant('{app}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopAgent;
    RemoveFromUserPath(ExpandConstant('{app}'));
  end;
end;
