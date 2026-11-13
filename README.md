<div align="center">

# SwitchProWin

Nintendo Switch Pro 手柄的 Windows 驱动与控制软件 —— 通过 **USB / 蓝牙** 连接手柄，
映射为**虚拟 Xbox 360 手柄**，接入所有常规支持手柄的 Windows 游戏（XInput / DirectInput）。

`USB / 蓝牙` · `Joy-Con` · `体感瞄准` · `Flick Stick` · `多配置文件` · `中英双语`

[English](README.en.md) · [技术方案](方案.md)

</div>

---

## 目录

- [功能特性](#功能特性)
- [安装](#安装)
- [快速开始](#快速开始)
- [使用说明](#使用说明)
- [从源码构建](#从源码构建)
- [项目结构](#项目结构)
- [开发与测试](#开发与测试)
- [路线图](#路线图)
- [贡献](#贡献)
- [许可](#许可)
- [致谢](#致谢)

## 功能特性

### 连接与虚拟手柄

- 蓝牙 / USB 连接、热插拔、断线立即重连，自动读取 SPI 摇杆出厂校准
- **Joy-Con 支持**：配对模式（L+R 合并为一副虚拟手柄，体感取 L，震动双发）、单只独立模式、
  充电握把自动判别左右（SPI 设备类型）；设备页一键切换模式
- 虚拟 Xbox 360 手柄（ViGEmBus），端到端验证并经真实游戏实测（含反作弊游戏）
- **震动回传**：官方编码算法 + 心跳 + 连接确认震，强度滑条（安全上限保护）+ 四个测试按钮
- **HidHide 防双输入**：检测到驱动自动配置（白名单本程序 + 隐藏实体设备），游戏不再"看见两只手柄"

### 体感（陀螺仪）

- 右摇杆瞄准（累积 + 衰减）/ 鼠标平移（SendInput）双模式
- 激活键：按住 ZL / ZR / L / R / 摇杆 / HOME 才启用，或始终启用
- 漂移补偿：启动静止校零 + 静止 3 秒自动重校零 + 手动"校零"按钮（游戏中 L+R+「+」组合键亦可）
- **Flick Stick（甩杆瞄准）**：右摇杆指向即转向（推杆甩向该方向、按住持续旋转、变向增量小甩），
  灵敏度可调，与陀螺仪叠加
- 灵敏度 10%–300%、反转 X/Y、交换轴

### 配置系统

- 多配置文件：保存 / 新建 / 删除 / 切换，JSON 导入导出
- **按游戏自动切换**：绑定进程名，前台运行时自动激活对应配置
- **组合键宏**：L+R+「−」切换配置、L+R+「+」陀螺仪重定心（可在设备页开关，游戏内无需切出）
- 17 槽位点击捕获重映射 + 右键"连发"（~10Hz Turbo）
- Nintendo / Xbox 布局一键预设；摇杆死区 / 响应曲线（5 种曲线）；软件校准覆盖
- 摇杆校准向导：采集中心 + 推满行程两步完成，一键恢复本体校准

### 界面与运维

- Eden 风格浅色主题：矢量手柄实时视图（按下红高亮 + 触发器下沉动画），三栏自适应布局
- **界面语言**：中文 / English，默认跟随系统语言，设备页切换，重启后生效（日志与安装器同步双语）
- 日志页（时间戳 + 历史回放）+ 文件日志 `%APPDATA%\SwitchProWin\log.txt`
- 低电量托盘提醒（≤1/8 未充电）；闲置自动休眠（0–60 分钟，按键唤醒回连）
- 性能统计：报告率 Hz + 处理耗时（设备页实时显示）；热路径零分配
- 托盘常驻、单实例、开机自启；ViGEmBus 缺失友好引导
- Inno Setup 双语安装器（自动检测依赖并引导下载）

## 安装

**方式一：安装包**（推荐普通用户）

1. 安装 [ViGEmBus 驱动](https://github.com/nefarius/ViGEmBus/releases)（虚拟手柄内核驱动，必需；
   安装器也会自动检测并引导）
2. 运行 `SwitchProWin-Setup-*.exe`（中英双语，按系统语言自动选择），可选桌面快捷方式与开机自启
3. 建议同时安装 [HidHide](https://github.com/nefarius/HidHide/releases)（防双输入，安装器会引导）

**方式二：从源码运行**：见[从源码构建](#从源码构建)。

## 快速开始

1. 启动 SwitchProWin（托盘常驻，自动打开控制面板）
2. 连接手柄：
   - **USB**：直插
   - **蓝牙**：长按手柄背部配对键，在 Windows 蓝牙设置中配对
3. 手柄连上时会有一声"双短震"确认；窗口实时显示电量与双摇杆原始值
4. 打开任意支持手柄的游戏直接游玩

> ⚠️ 使用 Steam 的游戏请关闭"Steam Input"，避免输入被叠加处理。
> ⚠️ 不要同时运行 GUI 和 CLI：会同时存在两只虚拟手柄，游戏输入叠加。

## 使用说明

### 图形控制面板

| 页面 | 内容 |
|---|---|
| **控制器配置** | 顶栏选择输入设备与配置文件；左列按键映射 / 摇杆死区曲线 / 十字键；中列手柄实时视图 + 震动 + 体感；右列右摇杆 / Flick Stick / 按游戏自动切换 / 配置导入导出 |
| **设备** | 已连接设备卡片（电量 / 连接方式 / 校准状态 / 性能）；右侧全局设置：开机自启、闲置休眠、界面语言、Joy-Con 模式、组合键开关 |
| **日志** | 引擎日志实时输出（含历史回放），可清空 |

**语言切换**：设备页 → 常规 → 「语言 / Language」→ 选择后重启生效（程序会询问是否立即重启）。

### 命令行验证工具（SwitchProWinCli）

```bash
dotnet run --project src/SwitchProWin.Cli -c Release
```

| 键 | 功能 |
|---|---|
| `1` | 低频马达（XInput LargeMotor）测试震动 |
| `2` | 高频马达（XInput SmallMotor）测试震动 |
| `3` | 双马达震动 |
| `0` | 停止震动 |
| `V` | 创建 3 秒纯虚拟手柄（不开手柄也能验证 ViGEmBus） |
| `Q` | 退出 |

## 从源码构建

**环境要求**

1. Windows 10/11 x64
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
3. ViGEmBus 驱动（运行必需）：https://github.com/nefarius/ViGEmBus/releases

**构建与运行**

```bash
dotnet build SwitchProWin.sln -c Release
dotnet test  SwitchProWin.sln            # 协议/映射/配置/本地化单元测试

# 图形控制面板（托盘常驻，推荐日常使用）
start src/SwitchProWin.App/bin/Release/net8.0-windows/SwitchProWinApp.exe

# 验证控制台（无需界面）
dotnet run --project src/SwitchProWin.Cli -c Release
```

> ⚠️ 修改代码重新构建前，先退出正在运行的实例（GUI 在托盘右键退出；CLI 按 Q），
> 否则旧进程会锁定 DLL 导致编译失败。

**安装包制作**：`dotnet publish -c Release -r win-x64 --self-contained false -o publish` 后，
将发布目录与 `installer/setup.iss` 放一起用 ISCC.exe 编译（需 Inno Setup 6.3+，自带中文语言文件）。

## 项目结构

```
src/
├─ SwitchProWin.Protocol/   # 协议库：0x30/0x21/0x3F 解析、子命令/震动包构造、SPI 校准解码
├─ SwitchProWin.Mapper/     # 映射引擎：可配置按键表（Pro → Xbox360）、死区/曲线/径向处理、Flick Stick
├─ SwitchProWin.ViGEm/      # ViGEmBus 客户端封装（虚拟 X360 手柄生命周期）
├─ SwitchProWin.Engine/     # 设备管理（热插拔）、会话、AppConfig/ControllerProfile、引擎编排、本地化（zh/en）
├─ SwitchProWin.App/        # WPF 控制面板：Eden 风格配置页（矢量手柄/槽位捕获）、设备、日志
└─ SwitchProWin.Cli/        # 验证控制台
tests/
└─ SwitchProWin.Tests/      # 报文金标准测试 + 配置/本地化回归测试
installer/
└─ setup.iss                # Inno Setup 双语安装脚本
```

## 开发与测试

- 单元测试 **83 个**全通过：协议金标准（0x30 报文逐字节验算）、映射/校准、陀螺仪、Flick Stick、
  震动编码、配置往返、本地化（键表奇偶校验 / 语言切换 / 回退）
- 端到端验证：winmm 探针 130 次输入变化全通；真实游戏实测（含反作弊环境）
- 性能目标：热路径零分配，报告率与处理耗时实时可视

开发者自验步骤（确认驱动链路工作正常）：

1. 运行 CLI，连接手柄，确认日志出现：
   - `输入模式 -> 0x30: ACK` 等四条 ACK
   - `摇杆校准 OK L[cx=...] R[cx=...]`
   - `虚拟 Xbox 360 手柄已创建`
2. `Win+R` → `joy.cpl`：出现 "Controller (XBOX 360 For Windows)"，编号随按键高亮
3. 摇动实体手柄左摇杆，打开 joy.cpl 中虚拟手柄 → 属性，观察轴变化

（日志语言跟随界面语言设置，英文安装输出对应英文行。）

## 路线图

- [x] 核心：连接 / 虚拟手柄 / 震动 / 按键映射 / 摇杆处理（v1.0）
- [x] Joy-Con、Flick Stick、组合键宏、校准向导、按游戏切配置（v1.1）
- [x] 中英双语界面与安装器
- [ ] 初运行引导向导（ViGEmBus 一键安装 + 配对指引）
- [ ] CI（GitHub Actions）
- [ ] 自动更新检查
- [ ] 输入展示悬浮窗、延迟图表、虚拟 DS4

## 贡献

欢迎 Issue 与 PR：

1. Fork → 特性分支 → 改动附测试 → `dotnet test` 全绿后提交 PR
2. 提交信息建议遵循常规前缀（`feat:` / `fix:` / `docs:` / `refactor:` / `test:`）
3. 涉及协议或并发（读线程/合并器）的改动请在描述中说明验证方式

## 许可

- 本项目采用 **GPL-3.0**：`src/SwitchProWin.App/Controls/ProControllerShape.cs` 中的手柄轮廓数据
  取自 Eden 模拟器（GPL-3.0），发布时须采用 GPL-3.0（或兼容）许可证并保留该文件头部来源声明。
- 协议实现为原创（依据公开协议文档），未复制 BetterJoy 等 GPL 项目的代码。
- 仅用于与个人拥有的手柄配套使用；软件名与图标不得使用 Nintendo 商标。

## 致谢

- [Eden 模拟器](https://eden-emu.dev/) — 手柄轮廓数据与界面风格参考（GPL-3.0）
- [dekuNukem/Nintendo_Switch_Reverse_Engineering](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) — 协议文档
- [Linux hid-nintendo 驱动](https://docs.kernel.org/hid/hid-nintendo.html) — 协议参考
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) / [ViGEm.Client](https://www.nuget.org/packages/Nefarius.ViGEm.Client) — 虚拟手柄驱动
- [HidHide](https://github.com/nefarius/HidHide) — 防双输入
