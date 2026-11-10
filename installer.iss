; Script do Inno Setup para gerar o instalador (Setup.exe) do CartucheiraVideo.
; Compilado automaticamente pelo GitHub Actions (.github/workflows/build-installer.yml)
; a partir da pasta "publish" gerada pelo "dotnet publish".
;
; AJUSTE o nome do .exe principal (MyAppExeName) se o executável gerado tiver outro nome
; (normalmente é "CartucheiraVideo.Presentation.exe" ou "CartucheiraVideo.exe" —
;  confira dentro da pasta "publish" depois do primeiro build).

#define MyAppName "Cartucheira Vídeo"
#define MyAppExeName "CartucheiraVideo.Presentation.exe"
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
Name: "desktopicon"; Description: "Criar ícone na área de trabalho"; GroupDescription: "Ícones adicionais:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir Cartucheira Vídeo"; Flags: nowait postinstall skipifsilent
