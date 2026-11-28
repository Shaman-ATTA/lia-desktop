; Inno Setup — per-user install, no admin; in-place upgrade over same AppId
#ifndef MyAppVersion
#define MyAppVersion "0.7.6"
#endif
#ifndef MyAppPublishDir
#define MyAppPublishDir "..\publish\LiaDesktop"
#endif

#define MyAppName "Lia Assistant"
#define MyAppPublisher "ATTA CORP"
#define MyAppExeName "LiaDesktop.exe"

[Setup]
; Stable AppId = UpgradeCode equivalent — upgrades replace in place, one Programs entry
AppId={{A7C3E2F1-9B4D-4E8A-B2C1-5D6E7F8091A2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\LiaDesktop
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=LiaDesktop-{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\LiaDesktop.WinUI\src\LiaDesktop\Assets\app.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; In-place upgrade: reuse prior dir, close running app, allow same/older version silently
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
DirExistsWarning=no
CloseApplications=yes
CloseApplicationsFilter=LiaDesktop.exe
RestartApplications=no
; Same-version reinstall + downgrade must not error
AllowNoIcons=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked
Name: "autostart"; Description: "Запускать вместе с Windows (в трее, ждать «Лиа»)"; GroupDescription: "Автозапуск:"

[Files]
; ignoreversion: replace binaries on upgrade/downgrade/reinstall; never touch user data files
; (memory.json, lia-config.dpapi, vosk\, logs\ are app-created and not in this set)
Source: "{#MyAppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LiaDesktop"; ValueData: """{app}\{#MyAppExeName}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Interactive install: offer launch. Silent auto-update: /LAUNCHAFTER=1 relaunches after upgrade.
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Лиа"; Flags: nowait postinstall skipifsilent; Check: not ShouldLaunchAfter
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifdoesntexist; Check: ShouldLaunchAfter

[UninstallDelete]
; Only temp browser profile — never memory.json / settings / vosk / logs
Type: filesandordirs; Name: "{localappdata}\LiaDesktop\playwright-profile"

[Code]
function CmdHasLaunchAfter: Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
  begin
    if CompareText(ParamStr(i), '/LAUNCHAFTER=1') = 0 then
    begin
      Result := True;
      Exit;
    end;
    if CompareText(ParamStr(i), '/LAUNCHAFTER') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function ShouldLaunchAfter: Boolean;
begin
  Result := CmdHasLaunchAfter;
end;

function InitializeSetup(): Boolean;
begin
  { Allow install over same or older version without aborting. }
  Result := True;
end;

(* Closing: CloseApplications (+ /FORCECLOSEAPPLICATIONS) only targets processes
   that hold files under the install dir. A portable/selftest copy elsewhere stays up. *)
