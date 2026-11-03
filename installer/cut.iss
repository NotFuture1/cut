#define AppName "Cut"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; every release uses the same AppId, so running a newer setup installs straight over the
; old one: same folder, no uninstall, no questions. a running cut is closed for you.
[Setup]
AppId={{7C7E1F3A-2B6D-4E8A-9C41-0AF3D8C21B57}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=future
AppPublisherURL=https://github.com/NotFuture1/cut
AppUpdatesURL=https://github.com/NotFuture1/cut/releases
DefaultDirName={autopf}\Cut
DefaultGroupName=Cut
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
CloseApplications=force
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
OutputBaseFilename=cut-setup-{#AppVersion}
OutputDir=Output
SetupIconFile=..\src\Assets\cut.ico
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayIcon={app}\cut.exe
UninstallDisplayName=Cut

[Tasks]
Name: desktopicon; Description: "create a desktop icon"; Flags: unchecked
Name: fileassoc; Description: "add ""open with cut"" for video files"; Flags: unchecked

; old builds can leave files the new one no longer ships (runtime dlls, static ffmpeg);
; clear them first so an upgrade is identical to a fresh install. settings and saved
; edits live in %localappdata%\cut and are never touched.
[InstallDelete]
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.json"
Type: files; Name: "{app}\ffmpeg.exe"
Type: files; Name: "{app}\ffprobe.exe"
Type: files; Name: "{app}\cut.exe"

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
; in-app updates run setup silently; reopen cut when they finish
Filename: "{app}\cut.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent
