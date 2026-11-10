; Script do Inno Setup para gerar o instalador (Setup.exe) do CartucheiraVideo.
; Compilado automaticamente pelo GitHub Actions (.github/workflows/build-installer.yml)
; a partir da pasta "publish" gerada pelo "dotnet publish".
;
; AJUSTE o nome do .exe principal (MyAppExeName) se o executÃ¡vel gerado tiver outro nome
; (normalmente Ã© "CartucheiraVideo.exe" ou "CartucheiraVideo.exe" â€”
;  confira dentro da pasta "publish" depois do primeiro build).

#define MyAppName "Cartucheira VÃ­deo"
#define MyAppExeName "CartucheiraVideo.exe"
#define MyAppPublisher "Metropolitana"
#define MyPublishDir "publish"

[Setup]
AppName={#MyAppName}
AppVersion=1.0
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CartucheiraVideo
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=CartucheiraVideo-Setup
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar Ã­cone na Ã¡rea de trabalho"; GroupDescription: "Ãcones adicionais:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir Cartucheira VÃ­deo"; Flags: nowait postinstall skipifsilent
