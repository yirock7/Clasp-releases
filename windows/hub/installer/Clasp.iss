#ifndef AppVersion
  #error AppVersion must be provided by Build-Installer.ps1.
#endif
#ifndef AppSourceDir
  #error AppSourceDir must be provided by Build-Installer.ps1.
#endif

#define AppName "Clasp"
#define ReleasesUrl "https://github.com/yirock7/Clasp-releases/releases"

[Setup]
; Keep this identity unchanged so later releases upgrade the same per-user installation.
AppId=Clasp.AppleDeviceHub
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName=Clasp {#AppVersion}
AppPublisher=Clasp contributors
AppPublisherURL=https://github.com/yirock7/Clasp-releases
AppSupportURL=https://github.com/yirock7/Clasp-releases/issues
AppUpdatesURL={#ReleasesUrl}
AppComments=Clasp for Windows
AppCopyright=Copyright 2026 Clasp contributors
LicenseFile=..\..\..\LICENSE

DefaultDirName={localappdata}\Programs\Clasp
DefaultGroupName=Clasp
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
Uninstallable=yes
UninstallDisplayName=Clasp
UninstallDisplayIcon={app}\Clasp.exe
MinVersion=10.0.22000

; The current app/package is qualified only for native x64 Windows. Do not admit x64 emulation on ARM64 until it is qualified.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
CloseApplications=yes
RestartApplications=no

OutputBaseFilename=Clasp-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\AppleDeviceHub.App\Assets\AppIcon.ico
VersionInfoProductName=Clasp
VersionInfoDescription=Clasp per-user installer

[Messages]
WindowsVersionNotSupported=Windows 11 (64-bit) is required.

[Tasks]
Name: "desktopicon"; Description: "Add a shortcut to the desktop"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start Clasp when I sign in"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE"; Flags: ignoreversion

[InstallDelete]
; The program used to be AppleDeviceHub.exe. An upgrade installs Clasp.exe, so remove the old files (and the old compiled
; resources) rather than leave a second program behind. Settings live in %LOCALAPPDATA%, not here, and are not touched.
Type: files; Name: "{app}\AppleDeviceHub.exe"
Type: files; Name: "{app}\AppleDeviceHub.pri"
Type: files; Name: "{app}\AppleDeviceHub.pdb"
Type: files; Name: "{app}\AppleDeviceHub.dll"

[Icons]
Name: "{autoprograms}\Clasp\Clasp"; Filename: "{app}\Clasp.exe"; Parameters: "--transport=ble --show=flyout"; WorkingDir: "{app}"
Name: "{autoprograms}\Clasp\Check for updates"; Filename: "{#ReleasesUrl}"
Name: "{autoprograms}\Clasp\Uninstall Clasp"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Clasp"; Filename: "{app}\Clasp.exe"; Parameters: "--transport=ble --show=flyout"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Per-user sign-in entry. Same value the app writes from StartupRegistration (name "Clasp", quoted path,
; then --transport=ble --startup). The app owns this entry after install: Setup writes it only when the task is selected
; (a fresh interactive install, or the box ticked on purpose) and never deletes it on install or upgrade, so a silent
; update (where "checkedonce" leaves the task off) keeps whatever the user chose in the app. Uninstall removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Clasp"; ValueData: """{app}\Clasp.exe"" --transport=ble --startup"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Finish-page checkbox, ticked by default. Skipped for silent installs (CI, scripted upgrades).
Filename: "{app}\Clasp.exe"; Parameters: "--transport=ble --show=flyout"; WorkingDir: "{app}"; Description: "Open Clasp"; Flags: postinstall nowait skipifsilent
; In-app updates run Setup silently with /RELAUNCH, so Clasp reopens (to the tray) after upgrading.
Filename: "{app}\Clasp.exe"; Parameters: "--transport=ble --startup"; WorkingDir: "{app}"; Flags: nowait; Check: RelaunchRequested

[Code]
{ kernel32 is always present; these three let Setup wait on the running Clasp without any extra DLL. }
function OpenProcess(dwDesiredAccess: Cardinal; bInheritHandle: Boolean; dwProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  FilesInstalled: Boolean;

function RelaunchRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
      Result := True;
end;

{ In-app updates pass /WAITPID=<Clasp's process id>. 0 when the switch is missing or not a positive number. }
function WaitPid: Integer;
var
  I: Integer;
  Arg: String;
begin
  Result := 0;
  for I := 1 to ParamCount do
  begin
    Arg := ParamStr(I);
    if CompareText(Copy(Arg, 1, 9), '/WAITPID=') = 0 then
      Result := StrToIntDef(Copy(Arg, 10, Length(Arg)), 0);
  end;
end;

{ Clasp quits itself right after starting Setup. Wait (at most 20 s) until that process has really ended, so no file of
  the old version is still in use when files are replaced. If it does not end in time, Setup carries on and the
  Restart Manager (CloseApplications) closes what is left. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Pid: Integer;
  Handle: THandle;
begin
  Result := '';
  Pid := WaitPid;
  if Pid <= 0 then Exit;
  Handle := OpenProcess($00100000, False, Pid); { SYNCHRONIZE }
  if Handle = 0 then Exit; { already gone, or not ours to wait on }
  try
    WaitForSingleObject(Handle, 20000);
  finally
    CloseHandle(Handle);
  end;
end;

{ Upgrading from a version whose program was AppleDeviceHub.exe: Setup leaves an existing sign-in value alone (the task is
  off by default on upgrades), so it would keep pointing at the file that was just deleted. If the value starts with the old
  program in this install folder, point it at Clasp.exe and keep its arguments. Any other value is not ours and is left as is.
  (The app makes the same repair at startup; doing it here covers a person who never opens Clasp before signing out.) }
procedure RepairLegacyRunEntry;
var
  RunKey, Current, OldPart, Rest: String;
begin
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  if not RegQueryStringValue(HKCU, RunKey, 'Clasp', Current) then Exit;
  OldPart := '"' + ExpandConstant('{app}\AppleDeviceHub.exe') + '"';
  if CompareText(Copy(Current, 1, Length(OldPart)), OldPart) <> 0 then Exit;
  Rest := Copy(Current, Length(OldPart) + 1, Length(Current));
  RegWriteStringValue(HKCU, RunKey, 'Clasp', '"' + ExpandConstant('{app}\Clasp.exe') + '"' + Rest);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    FilesInstalled := True;
    RepairLegacyRunEntry;
  end;
end;

{ A silent update closes Clasp first. If Setup then fails or is cancelled before the new files are in, the [Run] relaunch
  never happens, so start the (still intact) installed Clasp again here. }
procedure DeinitializeSetup;
var
  Exe: String;
  ResultCode: Integer;
begin
  if (not RelaunchRequested) or FilesInstalled then Exit;
  try
    Exe := ExpandConstant('{app}\Clasp.exe');
    { A failed upgrade from a version before the rename still has the old program installed. }
    if not FileExists(Exe) then Exe := ExpandConstant('{app}\AppleDeviceHub.exe');
    if FileExists(Exe) then
      Exec(Exe, '--transport=ble --startup', ExtractFilePath(Exe), SW_SHOWNORMAL, ewNoWait, ResultCode);
  except
    { Nothing more can be done; the user can start Clasp from the Start menu. }
  end;
end;

function InitializeSetup: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.NTPlatform and (Version.ProductType = VER_NT_WORKSTATION);
  if not Result then
    SuppressibleMsgBox(
      'Windows 11 (64-bit) is required.',
      mbCriticalError, MB_OK, IDOK);
end;

{ The clasp:// link registration is removed only when its command is exactly what this installation's app writes
  (see ProtocolRegistration.BuildCommand: "<exe>" --uri="%1"), so a clasp key made by another program is left alone. }
procedure RemoveOwnProtocolRegistration;
var
  Command: String;
begin
  { Either the current program or, if Clasp was never started after an upgrade, the one from before the rename. }
  if RegQueryStringValue(HKCU, 'Software\Classes\clasp\shell\open\command', '', Command) and
     ((CompareText(Command, '"' + ExpandConstant('{app}\Clasp.exe') + '" --uri="%1"') = 0) or
      (CompareText(Command, '"' + ExpandConstant('{app}\AppleDeviceHub.exe') + '" --uri="%1"') = 0)) then
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\clasp');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Remove only Clasp's sign-in value even if startup was enabled in-app after Setup ran with the task unchecked. }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Clasp');
    RemoveOwnProtocolRegistration;
  end;
  { Data is deleted only after an explicit Yes in an interactive uninstall. A silent uninstall never touches it. }
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    if MsgBox('Also delete Clasp''s settings and battery history?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\Clasp'), True, True, True);
      { The folder used before the rename, in case it was never migrated. }
      DelTree(ExpandConstant('{localappdata}\AppleDeviceHub'), True, True, True);
    end;
end;
