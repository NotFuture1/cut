#define AppName "Cut"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{7C7E1F3A-2B6D-4E8A-9C41-0AF3D8C21B57}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=future
DefaultDirName={autopf}\Cut
DefaultGroupName=Cut
OutputBaseFilename=cut-setup-{#AppVersion}
OutputDir=Output
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\cut.exe
DisableProgramGroupPage=yes

[Tasks]
Name: desktopicon; Description: "create a desktop icon"; Flags: unchecked
Name: fileassoc; Description: "add ""open with cut"" for video files"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Cut"; Filename: "{app}\cut.exe"
Name: "{autodesktop}\Cut"; Filename: "{app}\cut.exe"; Tasks: desktopicon

[Registry]
; progid + OpenWithProgids per extension: shows up in "open with", never hijacks defaults
Root: HKA; Subkey: "Software\Classes\Cut.Video"; ValueType: string; ValueData: "video file"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Cut.Video\DefaultIcon"; ValueType: string; ValueData: """{app}\cut.exe"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Cut.Video\shell\open\command"; ValueType: string; ValueData: """{app}\cut.exe"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "Cut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc

[Run]
Filename: "{app}\cut.exe"; Description: "launch cut"; Flags: nowait postinstall skipifsilent
