; SwitchPro for Windows - Inno Setup 安装脚本
; 构建：发布 Release 后，将 src/SwitchPro.App/bin/Release/net8.0-windows 发布目录
; 与本脚本放一起，用 ISCC.exe 编译。
; 依赖检测：ViGEmBus（必需，缺失时打开下载页）、HidHide（可选）。

#define AppName "SwitchPro"
#define AppVersion "1.0.0"
#define AppPublisher "SwitchPro Project"
#define AppExeName "SwitchProApp.exe"

[Setup]
AppId={{8B7C6D5E-4A3F-4E2D-9C1B-SWPRO0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\SwitchPro
DefaultGroupName=SwitchPro
UninstallDisplayIcon={app}\{#AppExeName}
OutputBaseFilename=SwitchPro-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
LicenseFile=..\LICENSE

[Files]
; 先 dotnet publish -c Release -r win-x64 --self-contained false -o publish
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\SwitchPro"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\SwitchPro"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "附加选项:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "SwitchPro"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即运行 SwitchPro"; Flags: nowait postinstall skipifsilent

[Code]
function ViGEmInstalled(): Boolean;
var
  version: String;
begin
  Result := RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\ViGEmBus', 'DisplayName', version)
    or RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\ViGEmBus', 'ImagePath', version);
end;

function HidHideInstalled(): Boolean;
var
  version: String;
begin
  Result := RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\HidHide', 'DisplayName', version)
    or RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\HidHide', 'ImagePath', version);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not ViGEmInstalled() then
    begin
      if MsgBox('检测到未安装 ViGEmBus 驱动（虚拟手柄必需）。' + #13#10 +
                '现在打开下载页面安装吗？安装完成后需重启电脑。', mbConfirmation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://github.com/nefarius/ViGEmBus/releases/latest', '', '', SW_SHOWNORMAL, ewNoWait, rc);
    end;
    if not HidHideInstalled() then
    begin
      if MsgBox('建议安装 HidHide（防止游戏同时识别实体+虚拟手柄造成双输入）。' + #13#10 +
                '现在打开下载页面吗？', mbConfirmation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://github.com/nefarius/HidHide/releases/latest', '', '', SW_SHOWNORMAL, ewNoWait, rc);
    end;
  end;
end;
