; Snag's installer (Inno Setup 6.7+). build-release.ps1 publishes the app to a short temp folder
; (Inno Setup can't read paths over 260 characters) and runs:
;   ISCC /DAppVersion=1.2.3 /DAppDir=<that folder> installer\Snag.iss
;
; Installs to Program Files (asks for admin). Snag's tools, settings and history stay in
; %LOCALAPPDATA%\Snag so yt-dlp can keep itself updated without admin prompts.
;
; Snag's in-app updater runs this with /SILENT /RELAUNCH: a progress window only, then Snag reopens.
; For testing without admin: /CURRENTUSER installs to %LOCALAPPDATA%\Programs instead.

#ifndef AppVersion
  #error Pass the version: ISCC /DAppVersion=1.2.3 installer\Snag.iss
#endif

#ifndef AppDir
  #define AppDir "..\build\app"
#endif
#define Repo "https://github.com/realaxecut/Snag"

[Setup]
AppId={{BF0867FE-5A71-418D-8A9B-9F0F45975ACC}
AppName=Snag
AppVersion={#AppVersion}
AppVerName=Snag {#AppVersion}
AppPublisher=realaxecut
AppPublisherURL={#Repo}
AppSupportURL={#Repo}/issues
AppUpdatesURL={#Repo}/releases
DefaultDirName={autopf}\Snag
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Snag-Setup-{#AppVersion}
SetupIconFile=..\Snag\Assets\snag.ico
UninstallDisplayIcon={app}\Snag.exe
UninstallDisplayName=Snag
WizardStyle=modern dark
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Snag
VersionInfoDescription=Snag Setup
; The uninstaller offers to remove %LOCALAPPDATA%\Snag; that's per-user on purpose.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Snag"; Filename: "{app}\Snag.exe"
Name: "{autodesktop}\Snag"; Filename: "{app}\Snag.exe"; Tasks: desktopicon

[Run]
; Normal install: a "Open Snag" checkbox on the last page.
Filename: "{app}\Snag.exe"; Description: "Open Snag"; Flags: nowait postinstall skipifsilent
; Update from inside Snag: reopen it. Post-install entries run as the signed-in user, not as admin.
Filename: "{app}\Snag.exe"; Flags: nowait postinstall; Check: RelaunchAfterUpdate

[Code]
function RelaunchAfterUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  if not WizardSilent then
    Exit;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
      Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    Exit;
  DataDir := ExpandConstant('{localappdata}\Snag');
  if DirExists(DataDir) and
     (MsgBox('Also remove Snag''s tools, settings and download history from this PC?' + #13#10#13#10 +
             'Your downloaded videos are not touched.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(DataDir, True, True, True);
end;
