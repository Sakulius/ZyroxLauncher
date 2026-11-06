#ifndef AppVersion
  #error AppVersion fehlt
#endif

[Setup]
AppId={{7C4E2B1A-9D3F-4E6A-8B57-5A1D0F2C9E31}
AppName=Zyrox
AppVersion={#AppVersion}
AppVerName=Zyrox {#AppVersion}
AppPublisher=Sakulius
AppPublisherURL=https://github.com/Sakulius/Zyrox
DefaultDirName={localappdata}\Programs\Zyrox
DefaultGroupName=Zyrox
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Zyrox-Setup-{#AppVersion}
SetupIconFile=..\Assets\zyrox.ico
UninstallDisplayIcon={app}\Zyrox.exe
UninstallDisplayName=Zyrox
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\app\Zyrox.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Assets\Fonts\inter-license.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\Zyrox"; Filename: "{app}\Zyrox.exe"
Name: "{userdesktop}\Zyrox"; Filename: "{app}\Zyrox.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Zyrox.exe"; Description: "{cm:LaunchProgram,Zyrox}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\Zyrox.exe.old"
Type: files; Name: "{app}\Zyrox.exe.new"
