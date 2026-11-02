; HeliVMS installer (Inno Setup 6).
;
; Packages the SELF-CONTAINED publish output (scripts/publish-desktop.ps1):
; the .NET 10 Desktop Runtime, ASP.NET Core (built-in web console) and the
; bundled ffmpeg\ folder all travel inside - the target machine installs
; nothing else. Build it with scripts/build-installer.ps1, never by hand:
; the wrapper locates ISCC, extracts the version from Directory.Build.props
; and runs publish first so the payload is fresh.

#define AppName "HeliVMS"
#define AppPublisher "禾秝軟體開發團隊"
#define Root ".."
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6E7A2C90-8B4E-4B7D-9A2F-1C3D5E7F9A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; 資料根目錄是 C:\HeliVMSData（或 %LOCALAPPDATA% 降級），不在安裝資料夾內——
; 解除安裝只刪程式，錄影與索引永不動。
AppComments=Smart 32-channel NVR/VMS. Data lives in C:\HeliVMSData and survives uninstall.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\HeliVMS.App.exe
; MIT（本程式）＋隨包 GPL ffmpeg（ffmpeg\ 內含 NOTICE 與授權檔）。
LicenseFile={#Root}\LICENSE
OutputDir={#Root}\publish\Setup
OutputBaseFilename=HeliVMS-Setup-{#AppVersion}
SetupIconFile={#Root}\Assets\app.ico
; 預設每機安裝（Program Files）；可用 /CURRENTUSER 裝到使用者資料夾（測試用）。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 整份自包含輸出（含 .NET Runtime、ASP.NET Core 與隨包的 ffmpeg\）。
Source: "{#Root}\publish\HeliVMS\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\HeliVMS.App.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\HeliVMS.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: checkedonce

[Run]
; Web 主控台的 Kestrel 埠（web.port 預設 5080）：放行 TCP 入站，遠端才開得到網頁。
; 只在每機安裝（有管理權）時做；埠之後在設定中心改的話規則要自己跟著改。
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""HeliVMS Web Console (TCP 5080)"" dir=in action=allow protocol=TCP localport=5080"; Flags: runhidden; Check: IsAdminInstallMode
Filename: "{app}\HeliVMS.App.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""HeliVMS Web Console (TCP 5080)"""; Flags: runhidden; RunOnceId: "DelFirewall"

