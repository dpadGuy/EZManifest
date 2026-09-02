#define MyAppName "EZManifest"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "dpadGuy"
#define MyAppURL "https://github.com/dpadGuy/EZManifest"
#define MyAppExeName "EZManifest.exe"

[Setup]
AppId={{8E5C2F1A-4B7D-4E21-9A6C-2D7F91B4E318}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
OutputDir=installer
OutputBaseFilename=EZManifest-Setup-{#MyAppVersion}
SetupIconFile=EZManifest\Assets\EZManifestLogo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no
DisableFinishedPage=yes
; First install defaults to %LocalAppData%\Programs\EZManifest. Later Inno runs
; reuse that previous folder. The in-app updater passes /DIR= for the running exe.
; Library data stays in %LocalAppData%\EZManifest.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\SteamAutoCrack.CLI\*"; DestDir: "{app}\SteamAutoCrack.CLI"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "dpadGuy.EZManifest"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; AppUserModelID: "dpadGuy.EZManifest"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall
