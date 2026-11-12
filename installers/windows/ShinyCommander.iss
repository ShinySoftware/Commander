; The Windows installer. Built by the release workflow, once per architecture:
;
;   iscc /DAppVersion=1.2.0 /DFileVersion=1.2.0 /DArch=x64 /DSourceDir=<publish dir> /DOutputDir=<dir> ShinyCommander.iss
;
; Per-user, into %LOCALAPPDATA%\Programs\ShinyCommander, so nobody needs to be an administrator to
; install it. A file manager is the one app you want on a locked-down machine.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish output>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppExe "ShinyCommander.exe"

[Setup]
; Never change this: it is how an upgrade finds the copy it is replacing.
AppId={{8831EF8E-2E8E-4453-86E9-1623AA743C74}
AppName=ShinyCommander
AppVersion={#AppVersion}
AppVerName=ShinyCommander {#AppVersion}
AppPublisher=Shiny Software
AppPublisherURL=https://github.com/ShinySoftware/Commander
AppSupportURL=https://github.com/ShinySoftware/Commander/issues
AppUpdatesURL=https://github.com/ShinySoftware/Commander/releases
VersionInfoVersion={#FileVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\ShinyCommander
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=ShinyCommander
OutputDir={#OutputDir}
OutputBaseFilename=ShinyCommander-{#AppVersion}-windows-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Restart Manager closes a running copy for an upgrade - it would hold its own files open.
CloseApplications=yes
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ShinyCommander"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\ShinyCommander"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,ShinyCommander}"; Flags: nowait postinstall skipifsilent

; Settings live in the app's own data folder and are left alone: an uninstall to reinstall should
; not lose the hotlist, tabs and keymap preferences.
