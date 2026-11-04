; Per-user installer for YAMV. scripts/make-installer.ps1 passes every value below.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>; scripts/make-installer.ps1 does.
#endif
#ifndef NumericVersion
  #error Pass /DNumericVersion=<a.b.c>; scripts/make-installer.ps1 does.
#endif
#ifndef SourceExe
  #error Pass /DSourceExe=<path of the published yamv.exe>; scripts/make-installer.ps1 does.
#endif
#ifndef OutputDir
  #error Pass /DOutputDir=<folder>; scripts/make-installer.ps1 does.
#endif

#define AppGuid "{F735C579-4307-4186-BF70-73BEB8DF2F23}"

[Setup]
AppId={{#AppGuid}
AppName=YAMV
AppVersion={#AppVersion}
AppVerName=YAMV {#AppVersion}
AppPublisher=leftos
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\YAMV
PrivilegesRequired=lowest
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
; yes, not force: a YAMV that refuses to close (an unsaved theme edit) makes setup fail instead of losing the edit.
CloseApplications=yes
CloseApplicationsFilter=yamv.exe
RestartApplications=no
ChangesAssociations=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=YAMV-Setup
SetupIconFile=..\src\Yamv.App\Assets\yamv.ico
UninstallDisplayIcon={app}\yamv.exe
UninstallDisplayName=YAMV
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\YAMV"; Filename: "{app}\yamv.exe"

[UninstallRun]
Filename: "{app}\yamv.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterYamv"

[Code]
var
  FirstInstall: Boolean;
  RegisterResult: Integer;

function InitializeSetup(): Boolean;
begin
  FirstInstall := not RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1');
  RegisterResult := 0;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not Exec(ExpandConstant('{app}\yamv.exe'), '--register', '', SW_HIDE, ewWaitUntilTerminated, RegisterResult) then
      RegisterResult := -1;

    // Settings opens on a first install only, so a landing's silent reinstall never opens it.
    if FirstInstall and (RegisterResult = 0) then
      if not ShellExec('open', 'ms-settings:defaultapps?registeredAppUser=YAMV', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
        Log('Could not open Settings: ' + SysErrorMessage(ErrorCode));
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if RegisterResult <> 0 then
    Result := 8
  else
    Result := 0;
end;
