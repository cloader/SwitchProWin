using System.Diagnostics;
using SwitchProWin.Mapper;
using SwitchProWin.Protocol;
using SwitchProWin.ViGEm;

namespace SwitchProWin.Engine;

/// <summary>
/// 一只 Switch Pro 手柄的会话：HID 读写、初始化、SPI 校准、状态推送、震动回传。
/// 线程模型：后台读线程 + 事件回调；输出写入（子命令/震动）用锁串行化。
/// </summary>
public sealed class ProControllerSession : IDisposable
{
    private const int SpiReadTimeoutMs = 1000;

    /// <summary>Windows 蓝牙 HID 设备路径中的服务 GUID（Human Interface Device Service）。</summary>
    private const string BtHidServiceGuid = "{00001124-0000-1000-8000-00805f9b34fb}";

    private readonly HidSharp.HidDevice _device;
    private HidSharp.HidStream? _stream;
    private Thread? _readThread;
    private volatile bool _running;

    private int _outputReportLength = ReportId.OutputReportLength;
    private bool _isBluetooth;

    private readonly object _writeLock = new();
    private readonly OutputReportBuilder.PacketCounter _counter = new();

    // 待等待的子命令应答（0x21）
    private readonly AutoResetEvent _replyEvent = new(false);
    private byte[]? _replyBuffer;
    private volatile byte _awaitingSubcommand;

    private VirtualXboxController? _virtualPad;
    private MappingProfile _profile = new();
    private PlayerLeds _leds = PlayerLeds.None;

    /// <summary>体感处理器（模式/灵敏度由配置注入）。</summary>
    public GyroProcessor Gyro { get; } = new();

    /// <summary>Flick Stick（甩杆瞄准）处理器。</summary>
    public FlickStickProcessor FlickStick { get; } = new();
    public bool FlickStickEnabled { get; set; }
    private double _flickAccumDeg;
    private const double StickPeriodSec = 1.0 / 120.0;

    /// <summary>配置注入后调用：体感重新启用时要求下一帧重新校零。</summary>
    public void MarkGyroNeedsCalibration() => _gyroNeedsCalibration = true;

    // ---- Turbo 连发 / 闲置休眠 ----

    /// <summary>连发键集合（配置注入）。</summary>
    public HashSet<ProButtons> TurboSet { get; } = new();

    /// <summary>闲置休眠（毫秒，0 = 关闭，配置注入）。</summary>
    public long IdleSleepMs { get; set; }

    private ProButtons _turboMask;
    private bool _turboPhase;
    private long _turboFlipMs;

    private ProButtons _lastInputButtons = ProButtons.None;
    private ushort _lastInputLX, _lastInputLY, _lastInputRX, _lastInputRY;
    private long _lastActivityMs;
    private bool _sleepSent;

    /// <summary>对输入施加 Turbo 通断与闲置检测（Map 之前调用，返回处理后的按钮状态）。</summary>
    private ProButtons ApplyTurboAndIdle(ProControllerState state)
    {
        long now = Environment.TickCount64;

        // 闲置检测：任意按键变化或摇杆移动 > 40（≈1% 行程）视为活动
        bool inputChanged = state.Buttons != _lastInputButtons
            || Math.Abs(state.LeftStick.X - _lastInputLX) > 40
            || Math.Abs(state.LeftStick.Y - _lastInputLY) > 40
            || Math.Abs(state.RightStick.X - _lastInputRX) > 40
            || Math.Abs(state.RightStick.Y - _lastInputRY) > 40;
        if (inputChanged)
        {
            _lastActivityMs = now;
            _sleepSent = false;
        }
        _lastInputButtons = state.Buttons;
        _lastInputLX = state.LeftStick.X; _lastInputLY = state.LeftStick.Y;
        _lastInputRX = state.RightStick.X; _lastInputRY = state.RightStick.Y;

        // 闲置休眠
        if (IdleSleepMs > 0 && !_sleepSent && now - _lastActivityMs > IdleSleepMs)
        {
            _sleepSent = true;
            Log?.Invoke(Loc.F("log.idleSleep", IdleSleepMs / 60000));
            try { WritePacket(OutputReportBuilder.SetShutdown(_counter.Next())); }
            catch { /* 设备可能已断开 */ }
            return state.Buttons & ~_turboMask;
        }

        // Turbo：按住时 ~10Hz 通断
        var buttons = state.Buttons;
        if (_turboMask != 0 && (buttons & _turboMask) != 0)
        {
            if (now - _turboFlipMs > 100)
            {
                _turboPhase = !_turboPhase;
                _turboFlipMs = now;
            }
            if (!_turboPhase)
                buttons &= ~_turboMask;
        }
        return buttons;
    }

    /// <summary>配置注入后刷新 Turbo 掩码。</summary>
    public void RefreshTurboMask()
    {
        _turboMask = 0;
        foreach (var b in TurboSet) _turboMask |= b;
    }

    /// <summary>设置玩家 LED（0..3 → LED1..4）。</summary>
    public void SetPlayerLeds(int playerIndex)
    {
        var leds = PlayerLedsFromIndex(playerIndex);
        try
        {
            WritePacket(OutputReportBuilder.SetPlayerLeds(_counter.Next(), leds));
            _leds = leds;
        }
        catch (Exception ex)
        {
            Log?.Invoke(Loc.F("log.ledFailed", ex.Message));
        }
    }

    // 震动状态（游戏回传 + 手动测试共用）
    private readonly object _rumbleLock = new();
    private (byte Large, byte Small) _rumbleLevel = (0, 0);
    private long _lastRumbleSentMs;
    private bool _rumbleActive;

    /// <summary>手感上限：XInput 满幅对应的相对幅度（0..1，默认 0.5；实测 1.0 过强）。</summary>
    public float RumbleMaxAmplitude { get; set; } = RumbleEncoder.DefaultMaxAmplitude;

    public DevicePathPath Path { get; }
    public StickCalibration LeftStickCalibration { get; private set; } = StickCalibration.Default;
    public StickCalibration RightStickCalibration { get; private set; } = StickCalibration.Default;
    public bool CalibrationLoaded { get; private set; }

    /// <summary>玩家编号（0..3，对应玩家 LED；断线重连可能变化）。</summary>
    public int PlayerIndex { get; private set; }

    /// <summary>是否蓝牙连接（否则 USB）。</summary>
    public bool IsBluetooth => _isBluetooth;

    /// <summary>最近一帧解析状态（读线程写入，UI 轮询用；未收到数据时为 null）。
    /// 普通 field：UI 侧允许读到略旧帧。</summary>
    public ProControllerState? LatestState;

    /// <summary>解析后、映射前的手柄状态（UI/日志用）。</summary>
    public event Action<ProControllerState>? StateReceived;

    /// <summary>映射后的虚拟手柄状态。</summary>
    public event Action<Xbox360State>? VirtualStateChanged;

    /// <summary>初始化/校准日志。</summary>
    public event Action<string>? Log;

    /// <summary>会话异常终止（设备拔出/读错误）。由管理器负责清理与重扫。</summary>
    public event Action<ProControllerSession>? Closed;

    public ProControllerSession(HidSharp.HidDevice device, ControllerKind? kindHint = null)
    {
        _device = device;
        Path = new DevicePathPath(device.DevicePath);
        _kindHint = kindHint;
    }

    private readonly ControllerKind? _kindHint;

    /// <summary>手柄种类（Start 后确定；握把经 SPI 判别 L/R）。</summary>
    public ControllerKind Kind { get; private set; } = ControllerKind.ProController;

    /// <summary>是否 Joy-Con（单摇杆，摇杆数据位于报告左摇杆槽位）。</summary>
    public bool IsJoyCon => Kind is ControllerKind.JoyConLeft or ControllerKind.JoyConRight;

    public string DevicePath => Path.Value;

    /// <summary>打开设备并完成初始化。返回 false 表示打开失败。
    /// createPad=false 时本会话不创建虚拟手柄（Joy-Con 配对模式由合并器统一输出）。</summary>
    public bool Start(int playerIndex, bool createPad = true)
    {
        if (!_device.TryOpen(out HidSharp.HidStream? stream))
        {
            Log?.Invoke(Loc.F("log.hidOpenFailed", _device.DevicePath));
            return false;
        }
        _stream = stream;
        _outputReportLength = Math.Max(_device.GetMaxOutputReportLength(), 1);
        _lastActivityMs = Environment.TickCount64; // 闲置计时从连接时刻起算（避免初值 0 立即休眠）
        // 设备路径含蓝牙 HID 服务 GUID => 蓝牙连接
        _isBluetooth = _device.DevicePath.Contains(BtHidServiceGuid, StringComparison.OrdinalIgnoreCase);
        _leds = PlayerLedsFromIndex(playerIndex);
        PlayerIndex = playerIndex;

        try
        {
            // 读线程必须先于初始化启动：0x21 子命令应答由读线程捕获转发
            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "SwitchProWin.ReadLoop" };
            _readThread.Start();

            Initialize();
        }
        catch (Exception ex)
        {
            Log?.Invoke(Loc.F("log.initFailed", ex.Message));
            _running = false;
            stream.Dispose();
            _stream = null;
            return false;
        }

        // 虚拟手柄 + 震动回传（Joy-Con 配对模式由合并器统一输出，不在此创建）
        if (createPad)
        {
            CreateVirtualPad();
        }
        else
        {
            Log?.Invoke(Loc.S("log.noVirtualPad"));
        }

        // 防双输入：实体手柄对游戏隐藏（HidHide 已安装时生效）
        try
        {
            if (HidHideIntegration.EnsureHidden(_device.DevicePath, Environment.ProcessPath, m => Log?.Invoke($"[HidHide] {m}")))
                Log?.Invoke(Loc.S("log.hidHideEnabled"));
        }
        catch (Exception ex)
        {
            Log?.Invoke(Loc.F("log.hidHideError", ex.Message));
        }
        return true;
    }

    private void CreateVirtualPad()
    {
        if (_virtualPad is not null) return; // 幂等
        try
        {
            _virtualPad = new VirtualXboxController();
        }
        catch (DllNotFoundException ex)
        {
            Log?.Invoke(Loc.F("log.vigemMissing", ex.Message));
            Log?.Invoke(Loc.S("log.vigemInstallHint"));
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("ViGEm", StringComparison.OrdinalIgnoreCase)
                                    || ex.InnerException?.Message.Contains("ViGEm") == true)
        {
            Log?.Invoke(Loc.F("log.virtualPadFailed", ex.Message));
            Log?.Invoke(Loc.S("log.vigemStartHint"));
            throw;
        }
        _virtualPad.RumbleReceived += OnRumbleReceived;
        Log?.Invoke(Loc.F("log.virtualPadCreated", VirtualXboxController.VendorId, VirtualXboxController.ProductId));
    }

    /// <summary>撤回会话级虚拟手柄（握把判别为 Joy-Con 且处于配对模式时）。</summary>
    public void WithdrawVirtualPad()
    {
        var pad = _virtualPad;
        _virtualPad = null;
        if (pad is null) return;
        try { pad.Dispose(); }
        catch { /* ignore */ }
        Log?.Invoke(Loc.S("log.virtualPadUnplugged"));
    }

    // ---- 初始化序列：判别种类 -> 切 0x30 模式 -> 开 IMU -> 开震动 -> LED -> SPI 校准 ----

    private void Initialize()
    {
        // 种类：PID 已知直接用；充电握把（200E 双接口）SPI 读设备类型判别 L/R
        if (_kindHint is { } hinted)
        {
            Kind = hinted;
        }
        else
        {
            var type = SpiReadBytes(SpiAddress.DeviceType, 1);
            Kind = type is { Length: 1 }
                ? (type[0] & 0x7) switch
                {
                    1 => ControllerKind.JoyConLeft,
                    2 => ControllerKind.JoyConRight,
                    3 => ControllerKind.ProController,
                    _ => ControllerKind.ProController,
                }
                : ControllerKind.ProController;
            Log?.Invoke(Loc.F("log.gripType", type is { Length: 1 } ? $"0x{type[0]:X2}" : Loc.S("log.gripTypeUnknown")));
        }

        if (_isBluetooth)
        {
            // 蓝牙：Windows HID 栈下无需 0x80 UART 握手（0x80 不是描述符定义的
            // output report，写会被拒绝；直接发 0x01 子命令即可，与 BetterJoy 一致）。
            Log?.Invoke(Loc.S("log.btSkipHandshake"));
        }
        else
        {
            // USB：0x80 0x04 防止手柄超时回蓝牙。部分固件/驱动下该 ID 不可写，尽力而为。
            try
            {
                WritePacket(OutputReportBuilder.UsbKeepAlive());
                Thread.Sleep(30);
            }
            catch (Exception ex)
            {
                Log?.Invoke(Loc.F("log.usbKeepaliveSkipped", ex.Message));
            }
        }

        // 1. 切换到全量输入模式（失败重试一次）
        var ack = SendSubcommandRetry(() => OutputReportBuilder.SetInputReportMode(_counter.Next(), InputMode.StandardFull), Subcommand.SetInputReportMode);
        Log?.Invoke(Loc.F("log.inputMode", DescribeAck(ack)));
        Thread.Sleep(20);

        // 2. 开 IMU（体感数据，M4 体感映射使用）
        ack = SendSubcommandRetry(() => OutputReportBuilder.EnableImu(_counter.Next(), true), Subcommand.EnableImu);
        Log?.Invoke(Loc.F("log.imuEnabled", DescribeAck(ack)));
        Thread.Sleep(20);

        // 3. 开震动马达
        ack = SendSubcommandRetry(() => OutputReportBuilder.EnableVibration(_counter.Next(), true), Subcommand.EnableVibration);
        Log?.Invoke(Loc.F("log.rumbleEnabled", DescribeAck(ack)));
        Thread.Sleep(20);

        // 4. 玩家 LED
        ack = SendSubcommandRetry(() => OutputReportBuilder.SetPlayerLeds(_counter.Next(), _leds), Subcommand.SetPlayerLeds);
        Log?.Invoke(Loc.F("log.playerLed", _leds, DescribeAck(ack)));
        Thread.Sleep(20);

        // 5. 读摇杆校准（用户校准优先，出厂兜底）
        LoadStickCalibration();

        // 6. 连接确认：短促低频双震
        try
        {
            SetRumble(140, 0);
            Thread.Sleep(120);
            SetRumble(0, 0);
            Thread.Sleep(80);
            SetRumble(140, 0);
            Thread.Sleep(120);
            SetRumble(0, 0);
        }
        catch { /* 震动失败不影响主流程 */ }
    }

    /// <summary>带重试的子命令发送（NACK/超时各重试一轮）。每次重试重建包：计数器必须逐包递增。</summary>
    private byte[]? SendSubcommandRetry(Func<byte[]> buildPacket, byte subcommandId, int tries = 2)
    {
        byte[]? reply = null;
        for (int i = 0; i < tries; i++)
        {
            reply = SendSubcommandAcked(buildPacket(), subcommandId);
            if (reply is not null && reply.Length > 13 && (reply[13] & 0x80) != 0)
                return reply; // ACK
            Thread.Sleep(60);
        }
        return reply;
    }

    private void LoadStickCalibration()
    {
        try
        {
            var left = ReadStickCalibration(SpiAddress.LeftStickUser, SpiAddress.LeftStickFactory, factoryFirst: false);
            var right = ReadStickCalibrationRight(SpiAddress.RightStickUser, SpiAddress.RightStickFactory);

            if (left is { } l && right is { } r)
            {
                LeftStickCalibration = l;
                RightStickCalibration = r;
                _profile.LeftStickCalibration = l;
                _profile.RightStickCalibration = r;
                CalibrationLoaded = true;
                Log?.Invoke(Loc.F("log.calOk", l.CenterX, l.MinX, l.MaxX, r.CenterX, r.MinX, r.MaxX));
            }
            else
            {
                Log?.Invoke(Loc.S("log.calReadFailed"));
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke(Loc.F("log.calError", ex.Message));
        }
    }

    private StickCalibration? ReadStickCalibration(uint userAddr, uint factoryAddr, bool factoryFirst)
    {
        var user = SpiReadBytes(userAddr, 11);
        if (user is { } u && u[0] == 0xB2 && u[1] == 0xA1)
            return StickCalibration.Decode(u.AsSpan(2, 9));

        var factory = SpiReadBytes(factoryAddr, 9);
        return factory is { } f ? StickCalibration.Decode(f) : null;
    }

    private StickCalibration? ReadStickCalibrationRight(uint userAddr, uint factoryAddr)
    {
        var user = SpiReadBytes(userAddr, 11);
        if (user is { } u && u[0] == 0xB2 && u[1] == 0xA1)
            return StickCalibration.DecodeRightFactory(u.AsSpan(2, 9)); // 用户右摇杆与出厂顺序一致

        var factory = SpiReadBytes(factoryAddr, 9);
        return factory is { } f ? StickCalibration.DecodeRightFactory(f) : null;
    }

    // ---- SPI 读取 ----

    private byte[]? SpiReadBytes(uint address, byte length)
    {
        var packet = OutputReportBuilder.SpiRead(_counter.Next(), address, length);
        var reply = SendSubcommandAcked(packet, Subcommand.SpiRead);
        if (reply is null)
        {
            Log?.Invoke(Loc.F("log.spiTimeout", address));
            return null;
        }

        // reply 是完整 0x21 报告：先切出 ACK 与数据区（byte15 起），再解析 SPI 结构
        if (!InputReportParser.TryParseSubcommandReply(reply, out byte ack, out _, out var payload, out _))
        {
            Log?.Invoke(Loc.F("log.spiAckParseFailed", address));
            return null;
        }
        if ((ack & 0x80) == 0)
        {
            Log?.Invoke(Loc.F("log.spiNack", address, ack));
            return null;
        }
        if (!SubcommandReplyParser.TryParseSpiRead(payload, out uint addr, out var data))
        {
            Log?.Invoke(Loc.F("log.spiDataParseFailed", address));
            return null;
        }
        if (addr != address)
        {
            Log?.Invoke(Loc.F("log.spiAddrMismatch", address, addr));
            return null;
        }
        return data.ToArray();
    }

    /// <summary>发送子命令并等待对应 ID 的 0x21 应答；超时返回 null。</summary>
    private byte[]? SendSubcommandAcked(byte[] packet, byte subcommandId)
    {
        _replyBuffer = null;
        _replyEvent.Reset();          // 丢弃迟到的旧应答
        _awaitingSubcommand = subcommandId;

        WritePacket(packet);

        if (!_replyEvent.WaitOne(SpiReadTimeoutMs))
        {
            _awaitingSubcommand = 0;
            return null;
        }
        return _replyBuffer;
    }

    private static string DescribeAck(byte[]? reply)
    {
        if (reply is null) return Loc.S("log.ackNone");
        // ACK 字节位于 0x21 报告 byte13（MSB=1 为 ACK）
        byte ack = reply.Length > 13 ? reply[13] : (byte)0;
        bool ok = (ack & 0x80) != 0;
        return ok ? "ACK" : $"NACK(0x{ack:X2})";
    }

    // ---- 输入/输出 ----

    private void WritePacket(byte[] packet)
    {
        lock (_writeLock)
        {
            var stream = _stream;
            if (stream is null) return;

            // Windows HID 要求写缓冲与设备输出报告长度一致；不匹配时填充/截断
            if (packet.Length == _outputReportLength)
            {
                stream.Write(packet, 0, packet.Length);
            }
            else
            {
                var buf = new byte[_outputReportLength];
                Array.Copy(packet, buf, Math.Min(packet.Length, buf.Length));
                stream.Write(buf, 0, buf.Length);
            }
        }
    }

    private void ReadLoop()
    {
        var stream = _stream!;
        int readLen = _device.GetMaxInputReportLength();
        var buf = new byte[readLen];

        try
        {
            while (_running)
            {
                int n;
                try
                {
                    n = stream.Read(buf, 0, buf.Length);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception) when (!_running)
                {
                    break; // Dispose 引起的正常退出
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        Log?.Invoke(Loc.F("log.readLoopEnded", ex.Message));
                        Closed?.Invoke(this); // 设备拔出/链路断开
                    }
                    return;
                }

                if (n <= 0) continue;

                // 处理/心跳同样要兜异常：写震动时设备断开（IOException）、
                // Dispose 竞态等都会从这里抛出；未处理的后台线程异常会杀死整个进程
                try
                {
                    ProcessInput(buf.AsSpan(0, n));
                    RumbleHeartbeat();
                }
                catch (Exception) when (!_running)
                {
                    break; // Dispose 引起的正常退出
                }
                catch (Exception ex)
                {
                    Log?.Invoke(Loc.F("log.processLoopEnded", ex.Message));
                    Closed?.Invoke(this);
                    return;
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    private void ProcessInput(ReadOnlySpan<byte> report)
    {
        var sw = (_latencySampling || _latencyWarm) ? Stopwatch.StartNew() : null;

        switch (report[0])
        {
            case ReportId.StandardFull:
            {
                if (InputReportParser.TryParseStandardFull(report, LeftStickCalibration, RightStickCalibration,
                        ImuCalibration.Default, _imuBuffer, out var state))
                {
                    // Turbo/闲置处理后的按钮状态（仅影响映射输出；原始状态供 UI/合并器/组合键使用）
                    var mappedState = state with { Buttons = ApplyTurboAndIdle(state) };

                    // Flick Stick：右摇杆被征用为瞄准输入。注意不能把原始摇杆置 default——
                    // (0,0) 不是中心，NormalizeStick 会解出 (-1,-1) 使基线漂到左下；
                    // 正常映射后在结果上丢弃摇杆输出即可
                    (double fx, double fy) flickNorm = (0, 0);
                    if (FlickStickEnabled)
                        flickNorm = _profile.NormalizeRightStick(state.RightStick);

                    LatestState = mappedState with { RightStick = state.RightStick };
                    // 组合键检测/CLI 显示订阅的是原始状态（Turbo 通断会制造假按键沿）
                    StateReceived?.Invoke(state);
                    var mapped = _profile.Map(mappedState);
                    if (FlickStickEnabled)
                        mapped = mapped with { RX = 0, RY = 0 };
                    ApplyGyro(ref mapped, mappedState);
                    ApplyFlickStick(ref mapped, flickNorm);
                    VirtualStateChanged?.Invoke(mapped);
                    _virtualPad?.Update(mapped);
                    _reportCount++;
                }
                break;
            }

            case ReportId.SubcommandReply:
            {
                if (InputReportParser.TryParseSubcommandReply(report, out _, out byte subcmd, out _, out _)
                    && subcmd == _awaitingSubcommand)
                {
                    _replyBuffer = report.ToArray();
                    _awaitingSubcommand = 0;
                    _replyEvent.Set();
                }
                break;
            }

            case ReportId.SimpleHid:
            {
                if (InputReportParser.TryParseSimple(report, out var state))
                    StateReceived?.Invoke(state);
                break;
            }
        }

        if (sw is not null)
        {
            sw.Stop();
            RecordLatency(sw.Elapsed.TotalMilliseconds);
        }
    }

    // ---- 性能统计（处理耗时：解析→映射→提交） ----

    private readonly ImuSample[] _imuBuffer = new ImuSample[3]; // 复用 IMU 缓冲（零分配热路径）
    private readonly Stopwatch _perfWatch = Stopwatch.StartNew();
    private double _avgProcessMs = 0.5;       // EMA
    private double _maxProcessMs;
    private long _reportCount;
    private long _lastReportCount;
    private bool _latencyWarm = true;         // 首几帧抖动大，跳过
    private bool _latencySampling => _reportCount < 300; // 前 300 帧采样，之后不计时

    private void RecordLatency(double ms)
    {
        if (_latencyWarm)
        {
            _latencyWarm = false;
            return;
        }
        _avgProcessMs += (ms - _avgProcessMs) * 0.02; // EMA
        if (ms > _maxProcessMs) _maxProcessMs = ms;
        if (!_latencySampling) _maxProcessMs *= 0.999; // 缓慢回落
    }

    /// <summary>处理耗时 EMA（毫秒）：单帧 解析→映射→虚拟手柄提交。</summary>
    public double AvgProcessMs => _avgProcessMs;

    /// <summary>处理耗时峰值（毫秒）。</summary>
    public double MaxProcessMs => _maxProcessMs;

    /// <summary>实测报告率（Hz，按最近 1 秒报告数估算）。</summary>
    public double ReportHz
    {
        get
        {
            var elapsed = _perfWatch.Elapsed.TotalSeconds;
            if (elapsed < 1) return 0;
            var hz = (_reportCount - _lastReportCount) / elapsed;
            _lastReportCount = _reportCount;
            _perfWatch.Restart();
            return hz;
        }
    }

    // ---- 体感 ----

    /// <summary>体感需要重新校零（配置变更后下一帧触发；重连/初次启用时自动校零）。</summary>
    private bool _gyroNeedsCalibration = true;

    /// <summary>手动重新校零（UI 按钮）。用最近一帧 IMU 采样作为静止基准。</summary>
    public void CalibrateGyroNow()
    {
        var st = LatestState;
        if (st is { } s && s.Imu.Length > 0)
        {
            Gyro.CalibrateNow(s.Imu.Span);
            Log?.Invoke(Loc.S("log.gyroRecentered"));
        }
        else
        {
            _gyroNeedsCalibration = true; // 尚无 IMU 数据，等第一帧
            Log?.Invoke(Loc.S("log.gyroRecenterWaiting"));
        }
    }

    /// <summary>把陀螺仪输出叠加进虚拟手柄状态（右摇杆模式）或转成鼠标移动（鼠标模式）。</summary>
    private void ApplyGyro(ref Xbox360State mapped, ProControllerState state)
    {
        if (Gyro.Mode == GyroMode.Off)
            return;

        var imu = state.Imu.Span;
        if (imu.Length == 0)
            return;

        // 激活键门控：松开时不瞄准，累积量清零（下次瞄准从中心开始）
        if (Gyro.ActivationButton is { } key && !state.Buttons.HasFlag(key))
        {
            Gyro.Reset();
            return;
        }

        // 启用后的第一帧做静止校零
        if (_gyroNeedsCalibration)
        {
            Gyro.CalibrateNow(imu);
            _gyroNeedsCalibration = false;
        }

        if (Gyro.Mode == GyroMode.RightStick)
        {
            var (dx, dy) = Gyro.UpdateRightStick(imu);
            mapped = mapped with
            {
                RX = ClampSbyteAdd(mapped.RX, (int)Math.Round(dx * 127)),
                RY = ClampSbyteAdd(mapped.RY, (int)Math.Round(dy * 127)), // 方向不符时用"反转Y"校正
            };
        }
        else if (Gyro.Mode == GyroMode.Mouse)
        {
            var (mdx, mdy) = Gyro.UpdateMouse(imu);
            if (mdx != 0 || mdy != 0)
                MouseMover.MoveRelative((int)Math.Round(mdx), (int)Math.Round(mdy));
        }
    }

    private static sbyte ClampSbyteAdd(int current, int add)
        => (sbyte)Math.Clamp(current + add, -127, 127);

    /// <summary>Flick Stick：每帧角度增量累积（带衰减），叠加到虚拟右摇杆 X。</summary>
    private void ApplyFlickStick(ref Xbox360State mapped, (double X, double Y) norm)
    {
        if (!FlickStickEnabled) return;

        double deg = FlickStick.Update(norm.X, norm.Y, StickPeriodSec);
        _flickAccumDeg = Math.Clamp(_flickAccumDeg + deg, -90, 90) * 0.86; // 与陀螺仪瞄准同款衰减
        if (Math.Abs(_flickAccumDeg) < 0.2) _flickAccumDeg = 0;

        double offset = _flickAccumDeg / FlickStickProcessor.FullDeflection; // -2..2 比例，钳到满幅
        mapped = mapped with
        {
            RX = ClampSbyteAdd(mapped.RX, (int)Math.Round(Math.Clamp(offset, -1, 1) * 127)),
        };
    }

    // ---- 震动（游戏回传 + 手动测试共用） ----

    /// <summary>设置震动强度（0..255，XInput 语义：large=低频/左握把，small=高频/右握把）。</summary>
    public void SetRumble(byte large, byte small)
    {
        lock (_rumbleLock)
        {
            _rumbleLevel = (large, small);
        }
        FlushRumble(force: true);
    }

    private void OnRumbleReceived(byte large, byte small)
        => SetRumble(large, small);

    private void FlushRumble(bool force = false)
    {
        byte[] packet;
        bool stillActive;
        lock (_rumbleLock)
        {
            long now = Environment.TickCount64;
            if (!force && now - _lastRumbleSentMs < 10)
                return; // 节流：最快 ~100Hz
            _lastRumbleSentMs = now;
            packet = OutputReportBuilder.Rumble(_counter.Next(),
                RumbleEncoder.Encode(_rumbleLevel.Large, _rumbleLevel.Small, RumbleMaxAmplitude));
            stillActive = (_rumbleLevel.Large | _rumbleLevel.Small) != 0;
            _rumbleActive = stillActive;
        }
        WritePacket(packet);
    }

    /// <summary>震动心跳：持续震动时定期重发（读循环每帧调用，60-120Hz）。</summary>
    private void RumbleHeartbeat()
    {
        bool active;
        lock (_rumbleLock)
        {
            active = _rumbleActive;
        }
        if (active && Environment.TickCount64 - Interlocked.Read(ref _lastRumbleSentMs) > 100)
            FlushRumble();
    }

    private static PlayerLeds PlayerLedsFromIndex(int index) => index switch
    {
        0 => PlayerLeds.Led1,
        1 => PlayerLeds.Led2,
        2 => PlayerLeds.Led3,
        3 => PlayerLeds.Led4,
        _ => PlayerLeds.None,
    };

    public MappingProfile Profile => _profile;

    public void Dispose()
    {
        if (_running)
        {
            _running = false;
            // 尽力关掉震动马达
            try
            {
                lock (_rumbleLock) { _rumbleLevel = (0, 0); }
                FlushRumble(force: true);
            }
            catch { /* 设备可能已消失 */ }
        }
        try { _readThread?.Join(500); } catch { /* ignore */ }

        _virtualPad?.Dispose();
        _virtualPad = null;

        _stream?.Dispose();
        _stream = null;
        _replyEvent.Dispose();
    }
}

/// <summary>设备路径包装（保证引用语义）。</summary>
public sealed class DevicePathPath
{
    public string Value { get; }
    public DevicePathPath(string value) => Value = value;
}
