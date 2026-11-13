; SwitchProWin for Windows - Inno Setup 安装脚本（中文/英文双语，按系统语言自动选择）
; 构建：发布 Release 后，将 src/SwitchProWin.App/bin/Release/net8.0-windows 发布目录
; 与本脚本放一起，用 ISCC.exe 编译。
; 依赖检测：ViGEmBus（必需，缺失时打开下载页）、HidHide（可选）。
; 语言文件：中文翻译已随仓库 vendor 于 Languages\ChineseSimplified.isl
;           （取自 jrsoftware/issrc 官方 Unofficial 翻译），构建不依赖本机 Inno 语言包。

#define AppName "SwitchProWin"
#define AppVersion "1.1.0"
#define AppPublisher "SwitchProWin Project"
#define AppExeName "SwitchProWinApp.exe"

[Setup]
AppId={{8B7C6D5E-4A3F-4E2D-9C1B-SWPRO0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\SwitchProWin
DefaultGroupName=SwitchProWin
UninstallDisplayIcon={app}\{#AppExeName}
OutputBaseFilename=SwitchProWin-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
LicenseFile=..\LICENSE
ShowLanguageDialog=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "chs"; MessagesFile: "Languages\ChineseSimplified.isl"

[Files]
; 先 dotnet publish -c Release -r win-x64 --self-contained false -o publish
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\SwitchProWin"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\SwitchProWin"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[CustomMessages]
en.ExtraIcons=Additional icons:
chs.ExtraIcons=附加图标:
en.DesktopIcon=Create a &desktop shortcut
chs.DesktopIcon=创建桌面快捷方式
en.AutoStart=Start SwitchProWin automatically when Windows starts
chs.AutoStart=开机自动启动
en.LaunchNow=Launch SwitchProWin now
chs.LaunchNow=立即运行 SwitchProWin
en.ViGEmMissing=The ViGEmBus driver is not installed (required for the virtual controller).%n%nOpen the download page now? A restart is required after installation.
chs.ViGEmMissing=检测到未安装 ViGEmBus 驱动（虚拟手柄必需）。%n%n现在打开下载页面安装吗？安装完成后需重启电脑。
en.HidHideSuggest=HidHide is recommended (it hides the physical controller from games to prevent double input).%n%nOpen the download page now?
chs.HidHideSuggest=建议安装 HidHide（防止游戏同时识别实体+虚拟手柄造成双输入）。%n%n现在打开下载页面吗？

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:ExtraIcons}"
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:ExtraIcons}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "SwitchProWin"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

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
      if MsgBox(CustomMessage('ViGEmMissing'), mbConfirmation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://github.com/nefarius/ViGEmBus/releases/latest', '', '', SW_SHOWNORMAL, ewNoWait, rc);
    end;
    if not HidHideInstalled() then
    begin
      if MsgBox(CustomMessage('HidHideSuggest'), mbConfirmation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://github.com/nefarius/HidHide/releases/latest', '', '', SW_SHOWNORMAL, ewNoWait, rc);
    end;
  end;
end;
