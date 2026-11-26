; Inno Setup script for Twelve Testers Test Management.
; Build with ..\build-installer.ps1 (publishes the app, then runs ISCC with /DAppVersion=...).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Twelve Testers Tray"
#define AppExe "TwelveTestersTray.exe"
#define RunValue "TwelveTestersTray"

[Setup]
AppId={{B3F1C7A4-5D2E-4E88-9A16-7C4D0E9B2F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=bDevStudios
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\artifacts\installer
OutputBaseFilename=TwelveTestersTray-Setup-{#AppVersion}
SetupIconFile=..\TwelveTestersTray\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=bDevStudios.TwelveTestersTray.SingleInstance
CloseApplications=yes

[Tasks]
Name: "startup"; Description: "Start with Windows (in the system tray)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValue}"; \
    ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName} now"; Flags: nowait postinstall skipifsilent

; Uninstall removes only installed files and the Run value; the data folders are never touched.

[Code]
function HasDesktopRuntime10(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then FindClose(FindRec);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not HasDesktopRuntime10() then
    Result := SuppressibleMsgBox(
      'The .NET 10 Desktop Runtime was not found.' + #13#10 + #13#10 +
      'Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.' + #13#10 + #13#10 +
      'Continue anyway?', mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#RunValue}');
end;
