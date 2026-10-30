; HaloBar installer (Inno Setup 6). Build it with installer\build.ps1, which publishes a
; self-contained copy first, so the people you share it with don't need .NET installed.
;
; Installs per user (no admin prompt) into %LOCALAPPDATA%\Programs\HaloBar, the same place the
; development copy lives, adds a Start menu entry and an Apps & features entry with an
; uninstaller, and can start HaloBar with Windows.

#define AppName "HaloBar"
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif
#define AppExe "HaloBar.exe"

[Setup]
AppId={{6E7C2B9A-4F1D-4C8E-9B57-3A2D8F0C1E44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Keyur Patel
AppPublisherURL=https://github.com/keyurpatel22790/HaloBar
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=out
OutputBaseFilename=HaloBar-Setup-{#AppVersion}
SetupIconFile=..\src\HaloBar\Assets\HaloBar.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; HaloBar holds this mutex while it runs, so setup can ask to close it first.
AppMutex=HaloBar.SingleInstance
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start HaloBar when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Add a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "out\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The same Run value HaloBar's own "Start with Windows" switch uses.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start HaloBar now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close HaloBar before its files are removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopHaloBar"

[UninstallDelete]
; Settings, logs and the web profile stay in %APPDATA%\HaloBar unless the user removes them.
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // HaloBar's own switch may have written the Run value too; take it away with the app.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'HaloBar');
end;
