#ifndef ProductVersion
  #define ProductVersion "1.3.0"
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
ShowLanguageDialog=no
UninstallDisplayIcon={app}\LunarisDock.exe
OutputBaseFilename=LunarisDock-{#ProductVersion}-x64-Setup
VersionInfoVersion={#ProductVersion}
VersionInfoDescription=Установка Lunaris Dock
#if FileExists(SourcePath + "\..\src\LunarisDock.ico")
SetupIconFile=..\src\LunarisDock.ico
#endif

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\LunarisDock.exe"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(SourcePath + "\..\build\LunarisDock.exe.config")
Source: "..\build\LunarisDock.exe.config"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{userprograms}\Lunaris Dock"; Filename: "{app}\LunarisDock.exe"

[Run]
Filename: "{app}\LunarisDock.exe"; Description: "Запустить Lunaris Dock"; Flags: postinstall nowait skipifsilent

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
    Result := 'Закройте Lunaris Dock в остальных сеансах этой учётной записи и повторите установку.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseDock();
  if not Result then
    SuppressibleMsgBox('Закройте Lunaris Dock в остальных сеансах этой учётной записи и повторите удаление.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cmd, Exe: string;
begin
  if CurUninstallStep = usUninstall then begin
    Exe := ExpandConstant('{app}\LunarisDock.exe');
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LunarisDock', Cmd) then begin
      if (CompareText(Cmd, Exe) = 0) or (CompareText(Cmd, '"' + Exe + '"') = 0) then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LunarisDock');
    end;
  end;
end;
