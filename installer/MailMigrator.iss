; Installeur LEWEBMAX Mail Migrator (Inno Setup 6).
; Compilation : scripts\build-installer.ps1 (publie l'application autonome puis lance ISCC).
; Installation par utilisateur, sans droits administrateur ; .NET n'a pas besoin d'être installé.

#define AppName "LEWEBMAX Mail Migrator"
#ifndef AppVersion
  #define AppVersion "0.9.0"
#endif
#define AppExe "LewebmaxMailMigrator.exe"
#define SourceDir "..\publish\win-x64"

[Setup]
AppId={{8F3C2B1A-6E4D-4B7A-9C21-5A7E3D1F0B42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=LEWEBMAX
AppPublisherURL=https://github.com/Jepht3/email-migration
AppSupportURL=https://github.com/Jepht3/email-migration
VersionInfoVersion={#AppVersion}
VersionInfoCompany=LEWEBMAX
VersionInfoDescription=Installeur de {#AppName}
DefaultDirName={autopf}\LEWEBMAX\Mail Migrator
DefaultGroupName=LEWEBMAX
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=LEWEBMAX-MailMigrator-Setup-{#AppVersion}
SetupIconFile=..\src\MailMigrator.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Mise à jour : l'application ouverte est fermée proprement (les migrations reprennent au lancement suivant).
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Les données (historique, état des migrations, journaux, sauvegardes locales) sont dans
; %LOCALAPPDATA%\LEWEBMAX\MailMigrator et Documents\LEWEBMAX : elles sont conservées
; à la désinstallation, pour ne jamais perdre une sauvegarde ou un rapport.
