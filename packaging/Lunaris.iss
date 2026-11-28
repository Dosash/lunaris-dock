#ifndef ProductVersion
  #define ProductVersion "1.3.1.8"
#endif

[Setup]
AppId={{667F8C56-9E92-4C1A-8FA3-18D59F1A575D}
AppName=Lunaris Dock
AppVersion={#ProductVersion}
AppPublisher=Lunaris Dock
DefaultDirName={localappdata}\Programs\Lunaris Dock
DefaultGroupName=Lunaris Dock
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.22000
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
WizardStyle=modern
ShowLanguageDialog=yes
UsePreviousTasks=no
UninstallDisplayIcon={app}\LunarisDock.exe
OutputBaseFilename=LunarisDock-{#ProductVersion}-x64-Setup
VersionInfoVersion={#ProductVersion}
VersionInfoDescription=Lunaris Dock Setup
#if FileExists(SourcePath + "\..\src\LunarisDock.ico")
SetupIconFile=..\src\LunarisDock.ico
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
; Inno Setup 6.5.0+ translation by Zhenghan Yang, tag 6.5.0+ (1ace6a4).
; https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation
Name: "zhCN"; MessagesFile: "languages\ChineseSimplified.isl"

[CustomMessages]
en.StartAtSignIn=Start Lunaris Dock when I sign in to Windows
ru.StartAtSignIn=Запускать Lunaris Dock при входе в Windows
de.StartAtSignIn=Lunaris Dock bei der Windows-Anmeldung starten
es.StartAtSignIn=Iniciar Lunaris Dock al iniciar sesión en Windows
zhCN.StartAtSignIn=登录 Windows 时启动 Lunaris Dock
en.CloseForInstall=Close Lunaris Dock in the other sessions for this account, then try installing again.
en.CloseForUninstall=Close Lunaris Dock in the other sessions for this account, then try uninstalling again.
ru.CloseForInstall=Закройте Lunaris Dock в остальных сеансах этой учётной записи и повторите установку.
ru.CloseForUninstall=Закройте Lunaris Dock в остальных сеансах этой учётной записи и повторите удаление.
de.CloseForInstall=Schließen Sie Lunaris Dock in den anderen Sitzungen dieses Benutzerkontos und starten Sie die Installation erneut.
de.CloseForUninstall=Schließen Sie Lunaris Dock in den anderen Sitzungen dieses Benutzerkontos und starten Sie die Deinstallation erneut.
es.CloseForInstall=Cierre Lunaris Dock en las demás sesiones de esta cuenta y vuelva a intentar la instalación.
es.CloseForUninstall=Cierre Lunaris Dock en las demás sesiones de esta cuenta y vuelva a intentar la desinstalación.
zhCN.CloseForInstall=请关闭此帐户其他会话中的 Lunaris Dock，然后重新安装。
zhCN.CloseForUninstall=请关闭此帐户其他会话中的 Lunaris Dock，然后重新卸载。

[Tasks]
; The app can change this after installation. Mirror its current registration.
Name: "startup"; Description: "{cm:StartAtSignIn}"; Check: OwnsStartup
Name: "startup"; Description: "{cm:StartAtSignIn}"; Check: not OwnsStartup; Flags: unchecked

[Files]
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "languages\ChineseSimplified-LICENSE.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "Lucide-LICENSE.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "..\build\LunarisDock.exe"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(SourcePath + "\..\build\LunarisDock.exe.config")
Source: "..\build\LunarisDock.exe.config"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{userprograms}\Lunaris Dock"; Filename: "{app}\LunarisDock.exe"
Name: "{userdesktop}\Lunaris Dock"; Filename: "{app}\LunarisDock.exe"

[Registry]
Root: HKCU; Subkey: "Software\LunarisDock"; ValueType: string; ValueName: "InstallLanguage"; ValueData: "{language}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LunarisDock"; ValueData: """{app}\LunarisDock.exe"""; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LunarisDock"; Flags: deletevalue; Tasks: not startup; Check: OwnsStartup

[Run]
Filename: "{app}\LunarisDock.exe"; Description: "{cm:LaunchProgram,Lunaris Dock}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: files; Name: "{localappdata}\LunarisDock\Profile\settings.json"
Type: files; Name: "{localappdata}\LunarisDock\Profile\settings.json.bak"
Type: dirifempty; Name: "{localappdata}\LunarisDock\Profile"
Type: dirifempty; Name: "{localappdata}\LunarisDock\Recovery"
Type: dirifempty; Name: "{localappdata}\LunarisDock"

[Code]
function OpenImageForReplacement(FileName: string; Access, Share: DWORD; Security: Integer; Disposition, Flags: DWORD; TemplateFile: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseImage(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function OwnsStartup(): Boolean;
var
  Cmd, Exe: string;
begin
  Exe := ExpandConstant('{app}\LunarisDock.exe');
  Result := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LunarisDock', Cmd)
    and ((CompareText(Cmd, Exe) = 0) or (CompareText(Cmd, '"' + Exe + '"') = 0));
end;

function CloseDock(): Boolean;
var
  Exe: string;
  Status, I, F: Integer;
begin
  Result := True;
  Exe := ExpandConstant('{app}\LunarisDock.exe');
  if not FileExists(Exe) then exit;
  Result := Exec(Exe, '--exit', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Status);
  if not Result then exit;
  Result := Status = 0;
  if not Result then exit;
  Result := Exec(Exe, '--recover', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Status);
  if not Result then exit;
  Result := Status = 0;
  if not Result then exit;
  { Both Dock and its recovery process may still have the image mapped. }
  for I := 0 to 100 do begin
    F := OpenImageForReplacement(Exe, $C0000000, 0, 0, 3, $80, 0);
    if F <> -1 then begin CloseImage(F); exit; end;
    Sleep(100);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  if not CloseDock() then
    Result := CustomMessage('CloseForInstall');
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseDock();
  if not Result then
    SuppressibleMsgBox(CustomMessage('CloseForUninstall'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and OwnsStartup() then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LunarisDock');
end;
