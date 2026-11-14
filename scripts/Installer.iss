; Inno Setup script for MediaBoard. Build it with scripts\Build-Installer.ps1, which publishes the app first and passes
; AppVersion (such as 0.1.0-beta), VersionNumber (0.1.0.0), SourceDir (the published folder) and OutputDir.

#ifndef AppVersion
  #error Pass /DAppVersion=... (scripts\Build-Installer.ps1 does).
#endif
#ifndef VersionNumber
  #define VersionNumber "0.0.0.0"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=... (scripts\Build-Installer.ps1 does).
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{5E6296F9-320F-4736-A982-00DCBA33BA2C}
AppName=MediaBoard
AppVersion={#AppVersion}
AppVerName=MediaBoard {#AppVersion}
AppPublisher=feluminais
AppPublisherURL=https://github.com/feluminais/MediaBoard
AppSupportURL=https://github.com/feluminais/MediaBoard/issues
VersionInfoVersion={#VersionNumber}
; Per user: no administrator prompt. The program goes to %LOCALAPPDATA%\Programs\MediaBoard, apart from the data in
; %LOCALAPPDATA%\MediaBoard, so uninstalling never touches the library or settings.
PrivilegesRequired=lowest
DefaultDirName={autopf}\MediaBoard
DefaultGroupName=MediaBoard
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\MediaBoard.Desktop.exe
SetupIconFile=..\src\MediaBoard.Desktop\Assets\app.ico
LicenseFile=..\LICENSE
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=MediaBoard-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
; Stops a running MediaBoard (it may sit in the tray) before files are replaced, and starts it again after an update.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MediaBoard"; Filename: "{app}\MediaBoard.Desktop.exe"
Name: "{autodesktop}\MediaBoard"; Filename: "{app}\MediaBoard.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MediaBoard.Desktop.exe"; Description: "Start MediaBoard"; Flags: nowait postinstall skipifsilent
