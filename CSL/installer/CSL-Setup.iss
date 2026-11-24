#define MyAppName "CS:L Launcher"
#define MyAppVersion "0.3.0"
#define MyAppPublisher "CS:L Project"
#define MyAppExeName "CSL.exe"

[Setup]
AppId={{B4D9F5B1-8F58-4E74-9B2A-3F6E3D5B2C10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\CS:L
DefaultGroupName=CS:L
OutputDir=..\artifacts\installer
OutputBaseFilename=CSL-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README-CSL.txt"; Flags: isreadme

[Icons]
Name: "{group}\CS:L"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\CS:L"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить CS:L"; Flags: nowait postinstall skipifsilent
