; Installer Sanatate PC - Info PC Electric (Inno Setup 6)
; Instalare silentioasa (Microsoft Store): SanatatePC_Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{6F2B8E41-3C7A-4D19-9B5E-2A7C41D0E8F3}
AppName=Sănătate PC
AppVersion={#AppVersion}
AppVerName=Sănătate PC {#AppVersion}
AppPublisher=Info PC Electric S.R.L.
AppPublisherURL=https://www.infopc.ro
AppSupportURL=https://www.infopc.ro
AppCopyright=© 2026 Info PC Electric S.R.L.
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Info PC Electric S.R.L.
VersionInfoDescription=Sănătate PC - instalare
DefaultDirName={autopf}\Info PC Electric\Sanatate PC
DefaultGroupName=Sănătate PC
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=SanatatePC_Setup_{#AppVersion}
SetupIconFile=..\assets\sanatate.ico
UninstallDisplayIcon={app}\SanatatePC.exe
UninstallDisplayName=Sănătate PC
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
#ifexist "Romanian.isl"
Name: "ro"; MessagesFile: "Romanian.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\SanatatePC.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Sănătate PC"; Filename: "{app}\SanatatePC.exe"
Name: "{autodesktop}\Sănătate PC"; Filename: "{app}\SanatatePC.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SanatatePC.exe"; Description: "{cm:LaunchProgram,Sănătate PC}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
Type: files; Name: "{app}\licenta.spc"
Type: files; Name: "{app}\setari.ini"
Type: dirifempty; Name: "{app}"
