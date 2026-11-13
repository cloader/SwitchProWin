# SwitchPro for Windows

Switch Pro 手柄的 Windows 驱动与控制软件：通过 **USB / 蓝牙** 连接 Nintendo Switch Pro 手柄，
将其映射为**虚拟 Xbox 360 手柄**，从而接入所有常规支持手柄的 Windows 游戏（XInput / DirectInput）。

## 当前状态（v1.0 功能全量）

### 核心
- ✅ 蓝牙/USB 连接、热插拔、断线立即重连、SPI 摇杆校准读取
- ✅ **Joy-Con 支持**：配对模式（L+R 合并为一副虚拟手柄，体感取 L，震动双发）、
  单只独立模式、充电握把（SPI 判别 L/R）；设备页一键切换模式
- ✅ 虚拟 Xbox 360 手柄（ViGEmBus），winmm 端到端验证、真实游戏实测（含反作弊游戏）
- ✅ 震动回传（官方编码算法 + 心跳 + 连接确认震 + 强度滑条 + 测试按钮）
- ✅ HidHide 防双输入（检测到驱动自动配置：白名单本程序 + 隐藏实体设备）

### 体感（陀螺仪）
- ✅ 右摇杆瞄准（累积+衰减）/ 鼠标平移（SendInput）双模式
- ✅ 激活键（按住 ZL/ZR/L/R/摇杆/HOME 才启用，或始终启用）
- ✅ 漂移补偿：启动静止校零 + 静止 3 秒自动重校零 + 手动"校零"按钮
- ✅ **Flick Stick（甩杆瞄准）**：右摇杆指向即转向（推杆甩向该方向、按住持续旋转、
  变向增量小甩），灵敏度可调，与陀螺仪叠加
- ✅ 灵敏度 10%-300%、反转 X/Y、交换轴

### 配置系统
- ✅ 多配置文件（保存/新建/删除/切换）、配置导入导出（JSON）
- ✅ 按游戏自动切配置：绑定进程名，前台运行自动激活
- ✅ **组合键宏**：L+R+「−」切换配置、L+R+「+」陀螺仪重定心（可在设备页开关）
- ✅ 17 槽位点击捕获重映射 + 右键"连发"（~10Hz Turbo）
- ✅ Nintendo/Xbox 一键预设；摇杆死区/曲线；软件校准覆盖
- ✅ 摇杆校准向导（采集中心 + 推满行程，可一键恢复本体校准）

### 界面与运维
- ✅ Eden 风格浅色主题：矢量手柄实时视图（Eden 轮廓数据，按下红高亮 + 触发器下沉动画）
- ✅ 自适应布局（三栏比例伸缩 + 手柄图 Viewbox 缩放）
- ✅ 日志页（时间戳 + 历史回放）+ 文件日志 `%APPDATA%\SwitchPro\log.txt`
- ✅ 低电量托盘提醒（≤1/8 未充电）；闲置自动休眠（0-60 分钟）
- ✅ 性能统计：报告率 Hz + 处理耗时（设备页实时显示）；热路径零分配
- ✅ ViGEmBus 缺失友好引导；托盘常驻、单实例、开机自启
- ✅ 单元测试 77 个全通过；Inno Setup 安装脚本（依赖检测引导）

### 许可
- 项目采用 **GPL-3.0**（手柄轮廓数据取自 Eden/GPL-3.0，见 `ProControllerShape.cs` 头部声明）

## 环境要求

1. Windows 10/11 x64
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（仅构建需要）
3. **ViGEmBus 驱动**（虚拟手柄内核驱动，本机已安装）
   - 下载：https://github.com/nefarius/ViGEmBus/releases
4. Switch Pro 手柄（蓝牙：长按配对键在系统蓝牙设置中配对；USB：直插）

## 构建与运行

```bash
dotnet build SwitchPro.sln -c Release
dotnet test  SwitchPro.sln            # 协议/映射/配置单元测试

# 图形控制面板（托盘常驻，推荐日常使用）
start src/SwitchPro.App/bin/Release/net8.0-windows/SwitchProApp.exe

# 验证控制台（无需界面）
dotnet run --project src/SwitchPro.Cli -c Release
```

> ⚠️ 不要同时运行 GUI 和 CLI：两只虚拟手柄会同时存在，游戏输入会叠加。
> 修改代码后重新构建前，先退出正在运行的实例（GUI 在托盘右键退出；CLI 按 Q）。

CLI 按键：

| 键 | 功能 |
|---|---|
| `1` | 低频马达（XInput LargeMotor）测试震动 |
| `2` | 高频马达（XInput SmallMotor）测试震动 |
| `3` | 双马达震动 |
| `0` | 停止震动 |
| `V` | 创建 3 秒纯虚拟手柄（不开手柄也能验证 ViGEmBus） |
| `Q` | 退出 |

手柄连接后窗口会实时显示电量与双摇杆原始值；连上时会有一声"双短震"确认。

## 验证步骤

1. 运行 CLI，连接手柄（蓝牙或 USB），确认日志出现：
   - `输入模式 -> 0x30: ACK` 等四条 ACK
   - `摇杆校准 OK L[cx=...] R[cx=...]`
   - `虚拟 Xbox 360 手柄已创建`
2. `Win+R` → `joy.cpl`：应出现一个 "Controller (XBOX 360 For Windows)"，编号随按键高亮
3. 摇动实体手柄左摇杆，点击 joy.cpl 里的虚拟手柄 → 属性 → 观察轴变化
4. 打开任意支持手柄的游戏直接游玩（如遇 Steam，请关闭"Steam Input"以免叠加处理）

## 项目结构

```
src/
├─ SwitchPro.Protocol/   # 协议库：0x30/0x21/0x3F 解析、子命令/震动包构造、SPI 校准解码
├─ SwitchPro.Mapper/     # 映射引擎：可配置按键表（Pro → Xbox360）、死区/曲线/径向处理
├─ SwitchPro.ViGEm/      # ViGEmBus 客户端封装（虚拟 X360 手柄生命周期）
├─ SwitchPro.Engine/     # 设备管理（热插拔）、会话、AppConfig/ControllerProfile、引擎编排
├─ SwitchPro.App/        # WPF 控制面板：Eden 风格配置页（矢量手柄/槽位捕获）、设备、日志
└─ SwitchPro.Cli/        # 验证控制台
tests/
└─ SwitchPro.Tests/      # 报文金标准测试 + 配置回归测试
```

## 关键实现笔记（踩坑记录）

- **蓝牙不需要 0x80 UART 握手**：Windows HID 栈下 0x80 不是描述符定义的 output report，
  中断 OUT 与控制通道写入都会被拒绝（ERROR_INVALID_PARAMETER）。直接发 0x01 子命令即可。
- **输出报告长度必须精确匹配设备能力**：USB 为 64 字节，蓝牙为 49 字节
  （`GetMaxOutputReportLength()`），不匹配时 WriteFile 报参数错误。
- **读线程必须先于初始化启动**：0x21 子命令应答由读线程捕获，按"子命令 ID 匹配"转发给等待者。
- **0x21 报文布局**：byte13=ACK（MSB=1 为 ACK）、byte14=子命令 ID、byte15 起为数据区；
  SPI 读的数据区为 `[地址 4B LE][长度 1B][数据]`。
- **输入报告**：Windows ReadFile 恒返回最大输入报告长度（362B），0x30 实际只有 50 字节有效。
- **摇杆"清零"陷阱**：置 `RightStick = default`（原始值 0,0）不等于回中——按中心 2048
  归一化会解出 (−1,−1)。Flick Stick 征用右摇杆时要丢弃的是映射**结果**（RX/RY=0），
  不是原始读数（v1.0.1 修复：此前甩杆模式基线恒偏左下）。
- **轮询快于报告率时禁止重复积分**：合并器线程 4ms 轮询、IMU 120Hz 报告，
  陀螺仪按"每帧积分固定 1/120s"设计——必须按报告 Timer 去重，否则转速翻倍。
- **震动编码**：频率 `enc=round(log2(f/10)×32)`，HF 段 16 位码 `(enc-0x60)×4`、LF 段码 `enc-0x40`；
  幅度三段对数（`4·log2(a×120)` / `16·log2(a×17)` / `32·log2(a×8.7)`）；
  LF 幅度 16 位值 = `奇数编码?0x8000:0 | 编码/2+0x40`（奇数编码在**高位字节**带 0x80 中间档标志）。
  幅度安全上限 1.0（编码 100），超出会损伤线性马达；手感上限默认 0.7。

## 协议参考

- [dekuNukem/Nintendo_Switch_Reverse_Engineering](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering)
- [Linux hid-nintendo 驱动](https://docs.kernel.org/hid/hid-nintendo.html)
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) / [ViGEm.Client](https://www.nuget.org/packages/Nefarius.ViGEm.Client)

## 许可与免责

- 本项目计划**开源发布**：`src/SwitchPro.App/Controls/ProControllerShape.cs` 中的手柄轮廓数据
  取自 Eden 模拟器（GPL-3.0），因此本项目发布时须采用 **GPL-3.0**（或兼容）许可证，
  建议仓库根目录放置 GPL-3.0 LICENSE 文件并保留该文件头部的来源声明。
- 协议实现为原创（依据公开协议文档），未复制 BetterJoy 等 GPL 项目的代码。
- 仅用于与个人拥有的手柄配套使用；软件名与图标不得使用 Nintendo 商标。
