; Script generated by the Inno Setup Script Wizard.
; SEE THE DOCUMENTATION FOR DETAILS ON CREATING INNO SETUP SCRIPT FILES!

#define MyAppName "WorkerControl"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Murillo Lazzaretti"
#define MyAppURL "https://github.com/MurilloLazzaretti/worker-control"
#define MyAppExeName "WorkerControl.exe"

[Setup]
; NOTE: The value of AppId uniquely identifies this application. Do not use the same AppId value in installers for other applications.
; (To generate a new GUID, click Tools | Generate GUID inside the IDE.)
AppId={{5AC75519-600A-4CB2-9389-16AC5F757663}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
;AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
LicenseFile=Y:\Prod\GitHub\Murillo\worker-control\LICENSE
; Uncomment the following line to run in non administrative install mode (install for current user only.)
;PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputBaseFilename=WorkerControl - Installer
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "Y:\Prod\GitHub\Murillo\worker-control\Installer\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "Y:\Prod\GitHub\Murillo\worker-control\Installer\ConfigWorkers.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "Y:\Prod\GitHub\Murillo\worker-control\Installer\ManagementStudio.exe"; DestDir: "{app}"; Flags: ignoreversion
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: " /install"; Flags: runhidden;
Filename: net.exe; Parameters: "start {#MyAppName}"; Description: "Starting Worker Control service"; Flags: runhidden;

[UninstallRun]
Filename: net.exe; Parameters: "stop {#MyAppName}"; Flags: runhidden;
Filename: "{app}\{#MyAppExeName}"; Parameters: " /uninstall"; Flags: runhidden;