; ClashBarX Windows 安装器（Inno Setup）
; 构建：windows/scripts/build-installer.ps1（dotnet publish self-contained + ISCC 编译）
; 安装位置：{localappdata}\Programs\ClashBarX（用户级——无需管理员权限，
; 与 HKCU 自启、%APPDATA%\ClashBarX 数据目录的用户级链路一致）

#define Version "1.0.0"
#define Publisher "ClashBarX"
#define PublishDir "publish"

[Setup]
AppId={{8B3C9F2A-6D5E-4A7B-9C1D-2E4F5A6B7C8D}
AppName=ClashBarX
AppVersion={#Version}
AppVerName=ClashBarX {#Version}
AppPublisher={#Publisher}
DefaultDirName={localappdata}\Programs\ClashBarX
DefaultGroupName=ClashBarX
UninstallDisplayName=ClashBarX
UninstallDisplayIcon={app}\ClashBarX.App.exe
Output=dist
OutputBaseFilename=ClashBarX-{#Version}-setup
SetupIconFile=app.ico
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
DisableProgramGroupPage=yes

[Files]
; self-contained 发布目录（含 .NET 运行时，用户无需另装 runtime）
Source: "{#PublishDir}\ClashBarX.App.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ClashBarX"; Filename: "{app}\ClashBarX.App.exe"
Name: "{group}\卸载 ClashBarX"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ClashBarX"; Filename: "{app}\ClashBarX.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务:"

[Run]
Filename: "{app}\ClashBarX.App.exe"; Description: "启动 ClashBarX(&L)"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时温和关闭运行中的实例（托盘常驻进程）
Filename: "{app}\ClashBarX.App.exe"; Parameters: "--exit"; Flags: runhidden; RunOnceId: "CloseApp"

[UninstallDelete]
; 只清应用自身；%APPDATA%\ClashBarX（配置/订阅/内核）与 %LOCALAPPDATA%\Programs 的安装
; 目录之外的用户数据一律保留（重装可恢复全部状态）
Type: filesandordirs; Name: "{app}"
