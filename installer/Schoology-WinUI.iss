#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef SourceDir
  #define SourceDir "publish\win-x64"
#endif

#define AppName "Schoology"
#ifndef AppVersion
  #define AppVersion GetEnv("APP_VERSION")
#endif
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
OutputBaseFilename=Schoology-{#AppVersion}-{#Architecture}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64 arm64
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
UninstallDisplayName=Schoology

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a Schoology WinUI desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Schoology"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Schoology"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Schoology WinUI"; Flags: postinstall nowait skipifsilent
