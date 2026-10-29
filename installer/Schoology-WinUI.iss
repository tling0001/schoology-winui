#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef SourceDir
  #define SourceDir "publish\win-x64"
#endif

#define AppName "Schoology WinUI"
#define AppVersion "2026.10.0-port.28-net10"
#define AppExeName "Schoology.WinUI.exe"

[Setup]
AppId={{2D38A5B1-5F8D-4CB7-9F30-8F6D2A01A7C2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Schoology Desktop Port
DefaultDirName={localappdata}\Programs\Schoology
DefaultGroupName=Schoology
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\installer-output
OutputBaseFilename=Schoology-WinUI-{#AppVersion}-{#Architecture}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64 arm64
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
UninstallDisplayName=Schoology WinUI

[Code]
function IsDotNet10Installed(): Boolean;
var
  FindRec: TFindRec;
  RuntimePath: String;
begin
  Result := False;
  RuntimePath := ExpandConstant('{autopf}\dotnet\shared\Microsoft.NETCore.App\10.*');
  if FindFirst(RuntimePath, FindRec) then begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not IsDotNet10Installed() then begin
    if MsgBox('Schoology WinUI requires the .NET 10 runtime, which is not bundled to keep the download small. Install .NET 10 Runtime from Microsoft before launching the app. Open the official download page now?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a Schoology WinUI desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Schoology WinUI"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Schoology WinUI"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Schoology WinUI"; Flags: postinstall nowait skipifsilent
