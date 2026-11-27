; Installer Sanatate PC - DigitalSecureIt (Inno Setup 6)
; Instalare silentioasa (Microsoft Store): SanatatePC_Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

#ifndef AppVersion
  #define AppVersion "1.6.0"
#endif

[Setup]
AppId={{6F2B8E41-3C7A-4D19-9B5E-2A7C41D0E8F3}
AppName=Sănătate PC
AppVersion={#AppVersion}
AppVerName=Sănătate PC {#AppVersion}
AppPublisher=DigitalSecureIt
AppCopyright=© 2026 DigitalSecureIt
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=DigitalSecureIt
VersionInfoDescription=Sănătate PC - instalare
DefaultDirName={autopf}\DigitalSecureIt\Sanatate PC
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
UsedUserAreasWarning=no

[Languages]
#ifexist "Romanian.isl"
Name: "ro"; MessagesFile: "Romanian.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\SanatatePC.exe"; DestDir: "{app}"; Flags: ignoreversion
; daca instalatorul ruleaza de pe stick si langa el e licenta, o preia pe acest calculator
Source: "{src}\licenta.spc"; DestDir: "{userappdata}\InfoPC\SanatatePC"; Flags: external skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\Sănătate PC"; Filename: "{app}\SanatatePC.exe"
Name: "{autodesktop}\Sănătate PC"; Filename: "{app}\SanatatePC.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SanatatePC.exe"; Description: "{cm:LaunchProgram,Sănătate PC}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
Type: files; Name: "{app}\licenta.spc"
Type: files; Name: "{app}\setari.ini"
Type: dirifempty; Name: "{app}"
