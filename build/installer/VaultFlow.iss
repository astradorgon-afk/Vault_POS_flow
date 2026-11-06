#ifndef AppVersion
  #error AppVersion must be supplied by scripts/build-desktop-installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by scripts/build-desktop-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by scripts/build-desktop-installer.ps1
#endif
#ifndef WebView2Installer
  #error WebView2Installer must be supplied by scripts/build-desktop-installer.ps1
#endif

[Setup]
AppId={{9E62F2E5-7880-43EF-A981-8DAA13409E43}
AppName=VaultFlow POS
AppVersion={#AppVersion}
AppPublisher=VaultFlow
DefaultDirName={localappdata}\Programs\VaultFlow POS
DefaultGroupName=VaultFlow POS
OutputDir={#OutputDir}
OutputBaseFilename=VaultFlow-POS-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\Pos.Client.exe
UsePreviousAppDir=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#WebView2Installer}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\VaultFlow POS"; Filename: "{app}\Pos.Client.exe"
Name: "{autodesktop}\VaultFlow POS"; Filename: "{app}\Pos.Client.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."; Flags: waituntilterminated; Check: not IsWebView2Installed
Filename: "{app}\Pos.Client.exe"; Description: "Launch VaultFlow POS"; Flags: nowait postinstall skipifsilent

[Code]
function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    Result := (Version <> '') and (Version <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
      Result := (Version <> '') and (Version <> '0.0.0.0');
end;
