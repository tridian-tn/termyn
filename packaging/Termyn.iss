; Termyn installer.
;
; Per-user throughout: installs under %LOCALAPPDATA%\Programs\Termyn, writes only to HKCU, and asks
; for no elevation. That is what the spec calls for, and it means Termyn can be installed on a
; managed machine by someone who is not an administrator.
;
; Built by packaging/build.ps1, which publishes the app first and passes the version in.

#ifndef AppVersion
  ; No fallback on purpose. A default here would be a second place the version lives, and compiling
  ; this directly would then produce an installer confidently mislabelled with a stale number.
  #error AppVersion must be passed in — build with packaging/build.ps1
#endif

#ifndef PublishDir
  ; Only for compiling the script by hand. build.ps1 passes the directory it actually published to,
  ; so what it builds and what it packages can't drift apart.
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Termyn"
#define AppPublisher "Tridian"
#define AppExe "Termyn.exe"
#define AppUrl "https://github.com/tridian-tn/termyn"

; The runtime Termyn is built against. Framework-dependent, so this has to be present.
#define DotnetChannel "10.0"
#define DotnetDownload "https://dotnet.microsoft.com/download/dotnet/10.0"

[Setup]
AppId={{8F3A6C21-5D74-4E0B-9C1E-7A2B4D6E8F03}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user: no UAC prompt, no shared install, nothing left in Program Files.
PrivilegesRequired=lowest
; Empty deliberately. With no override allowed this can't be talked into a machine-wide install from
; the command line or a dialog, so per-user is a property of the installer rather than of how it
; happened to be run.
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

OutputDir=..\artifacts
OutputBaseFilename=Termyn-{#AppVersion}-setup
SetupIconFile=..\assets\termyn.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The same value the app manages itself from its settings screen, written the same way — quoted
; path, --tray argument — so the two can't disagree about what launch-at-login means.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "Termyn"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]

{ ---- .NET runtime detection ------------------------------------------------------------------- }

{ Termyn is framework-dependent, so the .NET Desktop Runtime has to be there. The shared framework
  directory is the honest thing to look at: it is what the host actually loads, and unlike the
  registry it is the same on every install flavour. }
function DesktopRuntimeFound(): Boolean;
var
  Roots: array[0..1] of String;
  Found: TFindRec;
  I: Integer;
begin
  Result := False;

  { Termyn publishes win-x64, so it is the x64 runtime that has to be there. On an Arm64 machine
    that lives under dotnet\x64 — the plain path holds the Arm64 one, which this apphost can't use. }
  Roots[0] := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  Roots[1] := ExpandConstant('{commonpf64}\dotnet\x64\shared\Microsoft.WindowsDesktop.App');

  for I := 0 to GetArrayLength(Roots) - 1 do
  begin
    if DirExists(Roots[I]) then
    begin
      if FindFirst(Roots[I] + '\{#DotnetChannel}.*', Found) then
      begin
        try
          repeat
            { A pre-release build doesn't count: by default the host won't roll a release-versioned
              app forward onto one, so finding 10.0.0-preview here would be a false yes — and a
              false yes is the bad direction. It installs cleanly and then won't start. }
            if ((Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', Found.Name) = 0) then
            begin
              Result := True;
              Exit;
            end;
          until not FindNext(Found);
        finally
          FindClose(Found);
        end;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Answer: Integer;
  ErrorCode: Integer;
begin
  Result := True;
  if DesktopRuntimeFound() then
    Exit;

  { Nobody is there to answer. MsgBox is not suppressible — /SUPPRESSMSGBOXES doesn't reach it — so
    prompting here would hang an unattended install on a modal dialog until something killed it.
    Install anyway, which is the branch the interactive default prefers too, and say so in the log. }
  if WizardSilent() then
  begin
    Log('The .NET {#DotnetChannel} Desktop Runtime was not found. Installing anyway; Termyn will not start until it is present.');
    Exit;
  end;

  { Offered rather than forced: the runtime is a separate download and Termyn cannot install it
    without elevation, so the honest thing is to send the user to it and let them come back. }
  Answer := MsgBox(
    'Termyn needs the .NET {#DotnetChannel} Desktop Runtime, which does not appear to be installed.' + #13#10#13#10 +
    'Open the download page now?' + #13#10#13#10 +
    'Choose No to install Termyn anyway — it will not start until the runtime is present.',
    mbConfirmation, MB_YESNOCANCEL);

  if Answer = IDYES then
  begin
    ShellExec('open', '{#DotnetDownload}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end
  else if Answer = IDCANCEL then
    Result := False;
end;

{ ---- Closing a running instance ---------------------------------------------------------------- }

{ Termyn lives in the tray, so it is very likely running during an upgrade or an uninstall. Files in
  use would otherwise force a reboot to finish. }
function CloseRunningTermyn(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM "{#AppExe}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Give the shell a moment to release the tray icon and the file handles. }
  Sleep(700);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningTermyn();
  Result := '';
end;

{ ---- Uninstall: what to do with the user's data ------------------------------------------------- }

procedure InitializeUninstallProgressForm();
begin
  CloseRunningTermyn();
end;

{ Whether to delete the user's settings, token and cache.

  Asked when there is someone to ask. Running unattended it keeps them unless told otherwise with
  /REMOVEDATA=yes — deleting a token and a task cache is not something to do to someone silently
  because they scripted an uninstall, and keeping them is the recoverable half of the choice. }
function ShouldRemoveUserData(): Boolean;
var
  Requested: String;
begin
  Requested := LowerCase(ExpandConstant('{param:REMOVEDATA|}'));

  if (Requested = 'yes') or (Requested = 'true') or (Requested = '1') then
  begin
    Log('User data: removing, asked for on the command line.');
    Result := True;
    Exit;
  end;

  if (Requested = 'no') or (Requested = 'false') or (Requested = '0') then
  begin
    Log('User data: keeping, asked for on the command line.');
    Result := False;
    Exit;
  end;

  if UninstallSilent() then
  begin
    Log('User data: keeping, because this is an unattended uninstall and nobody was asked.');
    Result := False;
    Exit;
  end;

  Result := MsgBox(
    'Remove your Termyn settings and cached tasks?' + #13#10#13#10 +
    'This deletes the saved API token, the local task cache and the logs:' + #13#10 +
    ExpandConstant('{userappdata}\Termyn') + #13#10 +
    ExpandConstant('{localappdata}\Termyn') + #13#10#13#10 +
    'Choose No to keep them, so reinstalling picks up where you left off.' + #13#10#13#10 +
    'Nothing in your Todoist account is touched either way.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

  if Result then
    Log('User data: removing, at the user''s request.')
  else
    Log('User data: keeping, at the user''s request.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { After the program is gone, so the answer takes effect immediately. The token lives in here too,
    so removing it is a real logout. }
  if CurUninstallStep <> usPostUninstall then
    Exit;

  { Removed whoever wrote it. uninsdeletevalue only covers the entry this installer created, and the
    app writes the same key and value name when launch-at-login is turned on from its settings — so
    installing without the tick, enabling it later and then uninstalling used to leave a startup
    entry pointing at a binary that no longer exists. }
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Termyn');

  if not ShouldRemoveUserData() then
    Exit;

  DelTree(ExpandConstant('{userappdata}\Termyn'), True, True, True);
  DelTree(ExpandConstant('{localappdata}\Termyn'), True, True, True);
  Log('User data removed.');
end;
