; DeviceMaster — Inno Setup installer script.
; Produces DeviceMaster-Setup.exe (setup wizard with Start-menu shortcut, optional
; desktop/startup shortcuts, and an uninstaller). Built by build-installer.ps1, which
; passes the whole-number version via /DMyAppVersion.
;
; Manual build: install Inno Setup (https://jrsoftware.org/isdl.php), publish the UI
; (dotnet publish src/DeviceMaster.Ui -c Release -r win-x64 --self-contained
;  -p:PublishSingleFile=true -o dist/ui) then compile this script.

#ifndef MyAppVersion
  #define MyAppVersion "0"
#endif
#define MyAppName "DeviceMaster"
#define MyAppExe "DeviceMaster.exe"
#define MyAppPublisher "Elliot Borst"

[Setup]
AppId={{B7E2D9A4-3C61-4E8F-A52B-90D417C6E8AB}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/elliot-borst/DeviceMaster
DefaultDirName={localappdata}\DeviceMaster
DefaultGroupName=DeviceMaster
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=DeviceMaster-Setup
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
SetupIconFile=src\DeviceMaster.Ui\DeviceMaster.ico
; The in-app updater downloads this installer and runs it while DeviceMaster is open;
; offer to close the running copy so files can be replaced (no forced reboot).
CloseApplications=yes
RestartApplications=no

[Files]
Source: "dist\ui\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startupicon"; Description: "Run automatically when Windows starts"; GroupDescription: "Startup:"

[Icons]
Name: "{group}\DeviceMaster"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Uninstall DeviceMaster"; Filename: "{uninstallexe}"
Name: "{userdesktop}\DeviceMaster"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon
Name: "{userstartup}\DeviceMaster"; Filename: "{app}\{#MyAppExe}"; Parameters: "--minimized"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch DeviceMaster now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app registers highest-run-level scheduled tasks for silent elevation and autostart
; (elevated apps can't launch from shell:startup); clean both up on uninstall.
Filename: "schtasks"; Parameters: "/Delete /TN ""DeviceMaster"" /F"; Flags: runhidden; RunOnceId: "RemoveRunTask"
Filename: "schtasks"; Parameters: "/Delete /TN ""DeviceMaster Startup"" /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"
