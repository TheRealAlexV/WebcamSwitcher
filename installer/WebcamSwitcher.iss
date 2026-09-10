; WebcamSwitcher Inno Setup installer.
; Packages the self-contained WPF app + the virtual-camera media source DLL,
; and registers the DLL (HKLM COM) on install / unregisters on uninstall.

#define MyAppName "WebcamSwitcher"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "TheRealAlexV"
#define MyAppExeName "WebcamSwitcher.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=admin
OutputDir=..\artifacts\installer
OutputBaseFilename=WebcamSwitcher-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\WebcamSwitcher.Source.dll"""; Flags: runhidden; StatusMsg: "Registering virtual camera..."

[UninstallRun]
Filename: "{sys}\regsvr32.exe"; Parameters: "/u /s ""{app}\WebcamSwitcher.Source.dll"""; Flags: runhidden

[Tasks]
Name: "startup"; Description: "Run {#MyAppName} at Windows startup"; GroupDescription: "Additional tasks:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
