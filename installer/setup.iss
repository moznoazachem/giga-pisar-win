; Inno Setup script for Giga Pisar (Windows). Build: ISCC.exe setup.iss
; Expects the published app in ..\dist\app (dotnet publish output).

#define AppExe "GigaPisar.exe"
#define AppVersion GetVersionNumbersString("..\dist\app\GigaPisar.exe")
#define AppPublisher "Giga Pisar"
#define AppUrl "https://gigapisar.github.io"

[Setup]
AppId={{7E1B0C4E-6C2B-4B7C-9C57-2D1E1F8A5A10}
AppName={cm:AppName}
AppVersion={#AppVersion}
AppVerName={cm:AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\GigaPisar
DefaultGroupName={cm:AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=GigaPisar-Setup
SetupIconFile=..\src\Assets\app.ico
WizardImageFile=art\wizard-large-*.png
WizardSmallImageFile=art\wizard-small-*.png
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={cm:AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
DisableWelcomePage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AppName=Гига Писарь
english.AppName=Giga Pisar
russian.AutoStart=Запускать при входе в Windows
english.AutoStart=Start when I sign in to Windows
russian.Extra=Дополнительно:
english.Extra=Additional options:
russian.Launch=Запустить Гига Писарь
english.Launch=Launch Giga Pisar
russian.Uninstall=Удалить Гига Писарь
english.Uninstall=Uninstall Giga Pisar

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:Extra}"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{cm:AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:Uninstall}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/im {#AppExe} /f"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\GigaPisar"
Type: filesandordirs; Name: "{userappdata}\GigaPisar"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GigaPisar"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GigaPisar"; Flags: deletevalue; Tasks: not autostart
