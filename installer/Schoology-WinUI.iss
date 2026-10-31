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
#ifndef RuntimeInstallerUrl
  #define RuntimeInstallerUrl "https://aka.ms/windowsappsdk/1.8/1.8.260804001/windowsappruntimeinstall-x64.exe"
#endif
#ifndef RuntimeInstallerHash
  #define RuntimeInstallerHash ""
#endif
#ifndef RuntimeInstallerSize
  #define RuntimeInstallerSize "0"
#endif

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
Source: "{#RuntimeInstallerUrl}"; DestDir: "{tmp}"; DestName: "WindowsAppRuntimeInstall.exe"; ExternalSize: {#RuntimeInstallerSize}; Hash: "{#RuntimeInstallerHash}"; Flags: external download ignoreversion

[Icons]
Name: "{group}\Schoology"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\Schoology"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\WindowsAppRuntimeInstall.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App SDK runtime..."; Flags: waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "Launch Schoology WinUI"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Schoology.WinUI.exe /F"; Flags: runhidden waituntilterminated
Filename: "{app}\Schoology.WinUI.exe"; Parameters: "--unregister-notifications"; Flags: runhidden waituntilterminated
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v Schoology /f"; Flags: runhidden waituntilterminated
