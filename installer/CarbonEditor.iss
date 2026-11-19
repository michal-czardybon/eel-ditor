; Inno Setup script for Carbon Editor.
; Build with build-installer.ps1 (publishes the app, then runs ISCC), or: ISCC installer\CarbonEditor.iss
; after `dotnet publish` into publish\win-x64.

#define AppName "Carbon Editor"
#define AppExe "CarbonEditor.exe"
#define SourceExe "..\publish\win-x64\" + AppExe
#define AppVersion GetFileVersion(SourceExe)
#if AppVersion == ""
  #error Publish the application first (publish\win-x64\CarbonEditor.exe is missing).
#endif

[Setup]
AppId={{C2EC2A29-C318-4512-8F26-5906B3AA7A52}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no administrator rights needed); the user can choose "for all users".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\artifacts\installer
OutputBaseFilename=CarbonEditor-{#AppVersion}-setup
SetupIconFile=..\src\CarbonPad.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
polish.TaskContextMenu=Dodaj „Otwórz w Carbon Editor” do menu kontekstowego plików
english.TaskContextMenu=Add "Open with Carbon Editor" to the context menu of files
polish.TaskOpenWith=Dodaj Carbon Editor do listy „Otwórz za pomocą” dla plików tekstowych
english.TaskOpenWith=Add Carbon Editor to "Open with" for text files
polish.OpenWithVerb=Otwórz w Carbon Editor
english.OpenWithVerb=Open with Carbon Editor
polish.Integration=Integracja z Eksploratorem:
english.Integration=Explorer integration:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "contextmenu"; Description: "{cm:TaskContextMenu}"; GroupDescription: "{cm:Integration}"
Name: "openwith"; Description: "{cm:TaskOpenWith}"; GroupDescription: "{cm:Integration}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; "Carbon Editor" from Win+R and other programs.
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey

; Context menu of every file.
Root: HKA; Subkey: "Software\Classes\*\shell\CarbonEditor"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenWithVerb}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\CarbonEditor"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\CarbonEditor\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu

; "Open with" list: the application and the text formats it supports.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
; Text formats offered in "Open with" (the user still picks the default program in Windows).
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.md\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".log"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.log\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ini"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.ini\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".cfg"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.cfg\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".conf"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.conf\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".json"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.json\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".xml"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.xml\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".yaml"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.yaml\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".yml"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.yml\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".csv"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.csv\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".tsv"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.tsv\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".cs"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.cs\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".cpp"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.cpp\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".c"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.c\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".h"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.h\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".hpp"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.hpp\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".py"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.py\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".js"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.js\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.ts\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".html"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.html\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".htm"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.htm\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".css"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.css\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".sql"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.sql\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ps1"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.ps1\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".bat"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.bat\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".cmd"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.cmd\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".sh"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.sh\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".gitignore"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.gitignore\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".gitattributes"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.gitattributes\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".editorconfig"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.editorconfig\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".props"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.props\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".targets"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.targets\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".csproj"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.csproj\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".sln"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.sln\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".toml"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.toml\OpenWithList\{#AppExe}"; ValueType: none; Flags: uninsdeletekey; Tasks: openwith

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Settings and the session (%APPDATA%\Carbon Editor) are left in place on uninstall.
