; Compiled by tools/package-exe.ps1 using Inno Setup 6.3 or later.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppArchitecture
  #define AppArchitecture "x64"
#endif

[Setup]
AppId={{CC35011B-9452-4AB8-8656-EE7A737FDC85}
AppName=SurgeLink
AppVersion={#AppVersion}
AppPublisher=AviderMin
AppPublisherURL=https://github.com/AviderMin/SurgeLink
DefaultDirName={localappdata}\Programs\SurgeLink
DefaultGroupName=SurgeLink
DisableProgramGroupPage=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes
UsePreviousLanguage=yes
PrivilegesRequired=lowest
MinVersion=10.0.19044
#if AppArchitecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename=SurgeLink-Setup-{#AppVersion}-{#AppArchitecture}
SetupIconFile={#PayloadDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\SurgeLink.exe
LicenseFile={#PayloadDir}\LICENSE.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Languages\LICENSE.txt"; DestDir: "{app}\Licenses"; DestName: "InnoSetup-ChineseSimplified.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\SurgeLink"; Filename: "{app}\SurgeLink.exe"
Name: "{autodesktop}\SurgeLink"; Filename: "{app}\SurgeLink.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SurgeLink.exe"; Description: "{cm:LaunchProgram,SurgeLink}"; Flags: nowait postinstall skipifsilent
