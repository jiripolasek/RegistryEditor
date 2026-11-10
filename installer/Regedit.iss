#ifndef Arch
  #error Pass /DArch=x64 or /DArch=arm64
#endif
#ifndef Version
  #error Pass /DVersion from the evaluated project Version
#endif
#ifndef Publisher
  #error Pass /DPublisher from the evaluated project Company
#endif
#ifndef SourceDir
  #error Pass /DSourceDir with the self-contained publish directory
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installers"
#endif
#ifdef Unsigned
  #define FileSuffix "-unsigned"
#else
  #define FileSuffix ""
#endif
#if VER >= EncodeVer(7, 0, 0)
  #define BinaryVersion GetVersionNumbersString(AddBackslash(SourceDir) + "regedit2.exe")
#else
  #define BinaryVersion GetFileVersion(AddBackslash(SourceDir) + "regedit2.exe")
#endif

[Setup]
AppId={{D5F5A8C6-451C-4398-B7F5-194DBCA9BB3A}
AppName=Modern Registry Editor
AppVersion={#Version}
AppPublisher={#Publisher}
AppPublisherURL=https://jiripolasek.com
DefaultDirName={autopf}\JPSoftworks\Modern Registry Editor
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
#if Arch == "x64"
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
  #error Unsupported architecture
#endif
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Regedit-{#Version}-win-{#Arch}-Setup{#FileSuffix}
SetupIconFile=..\src\Regedit\Assets\AppIcon.ico
UninstallDisplayIcon={app}\regedit2.exe
VersionInfoVersion={#BinaryVersion}
VersionInfoDescription=Modern Registry Editor Setup
VersionInfoCopyright={#GetStringFileInfo(AddBackslash(SourceDir) + "regedit2.exe", "LegalCopyright")}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
#ifndef Unsigned
SignTool=RegeditStoreSign
SignedUninstaller=yes
SignedUninstallerDir={#SourceDir}\..\signed-uninstaller
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Modern Registry Editor"; Filename: "{app}\regedit2.exe"; AppUserModelID: "JPSoftworks.RegistryEditor"

[Registry]
Root: HKA; Subkey: "Software\Classes\regedit2"; ValueType: string; ValueName: ""; ValueData: "URL:Modern Registry Editor Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\regedit2"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\regedit2\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\regedit2.exe"",0"
Root: HKA; Subkey: "Software\Classes\regedit2\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\regedit2.exe"" ""%1"""

[Run]
Filename: "{app}\regedit2.exe"; Description: "Launch Modern Registry Editor"; Flags: nowait postinstall skipifsilent runasoriginaluser
