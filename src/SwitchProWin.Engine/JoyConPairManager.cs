using SwitchProWin.Mapper;
using SwitchProWin.Protocol;
using SwitchProWin.ViGEm;

namespace SwitchProWin.Engine;

/// <summary>Joy-Con 使用模式。</summary>
public enum JoyConMode
{
    /// <summary>L+R 合并为一副手柄（默认）。</summary>
    Pair,

    /// <summary>每只 Joy-Con 独立为一副手柄。</summary>
    Individual,
}

/// <summary>
/// Joy-Con 配对合并器：把一只 L + 一只 R 会话合并为一副虚拟 Xbox 360 手柄。
/// 按钮 OR 合并；L 摇杆→左、R 摇杆→右（Joy-Con 摇杆都位于报告左槽位）；
/// 体感取 L 的 IMU；震动回传同时驱动两只。
/// </summary>
public sealed class JoyConPairManager : IDisposable
{
    private readonly SwitchProWinEngine _engine;
    private readonly Action<string> _log;
    private readonly object _stateLock = new(); // Rebuild/Stop/RecenterGyro 互斥（多线程事件源）
    private Thread? _thread;
    private volatile bool _running;

    private VirtualXboxController? _pad;
    private readonly MappingProfile _profile = new();
    private readonly GyroProcessor _gyro = new();
    private readonly FlickStickProcessor _flick = new();
    private double _flickAccum;
    private Xbox360State _last;
    private ProControllerSession? _left;
    private ProControllerSession? _right;

    public JoyConPairManager(SwitchProWinEngine engine, Action<string> log)
    {
        _engine = engine;
        _log = log;
        engine.Devices.ControllerArrived += OnSessionsChanged;
        engine.Devices.ControllerRemoved += OnSessionsChanged;
        engine.ActiveProfileChanged += OnProfileChanged;
    }

    private void OnSessionsChanged(ProControllerSession _) => Rebuild();
    private void OnProfileChanged() => ApplyProfile();

    /// <summary>陀螺仪重定心（不重建虚拟手柄）。</summary>
    public void RecenterGyro()
    {
        lock (_stateLock)
        {
            var st = _left?.LatestState;
            if (st is { } s && s.Imu.Length > 0)
                _gyro.CalibrateNow(s.Imu.Span);
            else
                _gyro.FullReset();
        }
    }

    /// <summary>重算配对状态（会话增删时调用；多线程事件源，需加锁）。</summary>
    public void Rebuild()
    {
        lock (_stateLock)
        {
            RebuildLocked();
        }
    }

    private void RebuildLocked()
    {
        var joycons = _engine.Devices.Sessions.Where(s => s.IsJoyCon).ToList();
        var left = joycons.FirstOrDefault(s => s.Kind == ControllerKind.JoyConLeft);
        var right = joycons.FirstOrDefault(s => s.Kind == ControllerKind.JoyConRight);

        bool shouldRun = _engine.Config.JoyConMode == JoyConMode.Pair && left is not null && right is not null;
        _left = shouldRun ? left : null;
        _right = shouldRun ? right : null;

        if (!shouldRun)
        {
            StopLocked();
            return;
        }

        if (_pad is null)
        {
            try
            {
                _pad = new VirtualXboxController();
                _pad.RumbleReceived += OnRumble;
                _log(Loc.S("log.jcPaired"));
            }
            catch (Exception ex)
            {
                _log(Loc.F("log.jcMergeFailed", ex.Message));
                _pad = null;
                _left = _right = null;
                return;
            }
        }
        else
        {
            // 换了新会话：校零重置
            _gyro.FullReset();
        }

        // （重新）注入配置与两只 Joy-Con 的 SPI 摇杆校准
        _hasLTimer = false;
        ApplyProfile();

        var first = _left!.LatestState;
        if (first is { } fs && fs.Imu.Length > 0)
            _gyro.CalibrateNow(fs.Imu.Span);
        else
            _gyro.FullReset(); // 尚无数据，等自动校零

        if (_thread is null)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "JoyConPair.Loop" };
            _thread.Start();
        }

        // 两只同显玩家 1 LED
        try
        {
            _left!.SetPlayerLeds(0);
            _right!.SetPlayerLeds(0);
        }
        catch { /* 尽力而为 */ }
    }

    private void ApplyProfile()
    {
        var profile = _engine.ActiveProfile;
        profile.ApplyTo(_profile);

        // 摇杆校准：用户软件校准（向导产物）优先，否则注入各只 Joy-Con 的 SPI 校准。
        // R 的摇杆数据位于其报告左槽位，但 SPI 校准存于"右摇杆"地址
        if (_left is { } l)
            _profile.LeftStickCalibration = profile.LeftStickCalibrationOverride ?? l.LeftStickCalibration;
        if (_right is { } r)
            _profile.RightStickCalibration = profile.RightStickCalibrationOverride ?? r.RightStickCalibration;

        _gyro.Mode = profile.GyroMode;
        _gyro.Sensitivity = profile.GyroSensitivity;
        _gyro.InvertX = profile.GyroInvertX;
        _gyro.InvertY = profile.GyroInvertY;
        _gyro.SwapAxes = profile.GyroSwapAxes;
        _gyro.ActivationButton = profile.ResolveGyroActivation();
        _gyro.AutoCalibrate = profile.GyroAutoCalibrate;
        _flick.Sensitivity = profile.FlickStickSensitivity;
    }

    private void OnRumble(byte large, byte small)
    {
        try
        {
            _left?.SetRumble(large, small);
            _right?.SetRumble(large, small);
        }
        catch { /* 断开竞态 */ }
    }

    private void StopLocked()
    {
        _running = false;
        var thread = _thread;
        _thread = null;
        var pad = _pad;
        _pad = null;
        _gyro.FullReset();

        // Loop 线程不获取 _stateLock，持锁 Join 无死锁风险
        if (thread is not null)
        {
            try { thread.Join(300); } catch { /* ignore */ }
        }
        if (pad is not null)
        {
            try { pad.Dispose(); } catch { /* ignore */ }
            _log(Loc.S("log.jcStopped"));
        }
    }

    // ---- 帧门控：Loop 轮询(~250Hz)快于报告率(~120Hz)，同一帧重复喂给
    // 陀螺仪积分会把角速度算约 2 倍，必须按 L 的报告计数器去重 ----
    private byte _lastLTimer;
    private bool _hasLTimer;
    private const double ReportPeriodSec = 1.0 / 120.0;

    private void Loop()
    {
        while (_running && _pad is not null)
        {
            try
            {
                var ls = _left?.LatestState;
                var rs = _right?.LatestState;
                if (ls is { } l && rs is { } r)
                {
                    if (!_hasLTimer || l.Timer != _lastLTimer)
                    {
                        _hasLTimer = true;
                        _lastLTimer = l.Timer;
                        ProcessPairFrame(l, r);
                    }
                }
                else
                {
                    _hasLTimer = false; // 任一侧暂无数据：下次到达按新帧处理
                }
            }
            catch
            {
                // 瞬时异常继续循环；会话移除由 Rebuild 处理
            }
            Thread.Sleep(4);
        }
    }

    private void ProcessPairFrame(ProControllerState l, ProControllerState r)
    {
        var combined = new ProControllerState
        {
            Timer = l.Timer,
            BatteryLevel = Math.Min(l.BatteryLevel, r.BatteryLevel),
            Charging = l.Charging || r.Charging,
            IsJoyConType = true,
            Buttons = l.Buttons | r.Buttons,
            LeftStick = l.LeftStick,
            RightStick = r.LeftStick,   // Joy-Con R 的摇杆也在左槽位
            Imu = l.Imu,                // 体感取 L
        };

        // Flick Stick：征用右摇杆为瞄准输入。正常映射后丢弃摇杆输出
        // （不能把原始摇杆置 default——(0,0) 不是中心，会解出 (-1,-1) 基线漂移）
        (double X, double Y) flickNorm = (0, 0);
        bool flick = _engine.ActiveProfile.FlickStickEnabled;
        if (flick)
            flickNorm = _profile.NormalizeRightStick(combined.RightStick);

        var mapped = _profile.Map(combined);
        if (flick)
            mapped = mapped with { RX = 0, RY = 0 };

        ApplyGyro(ref mapped, combined);
        if (flick)
        {
            double deg = _flick.Update(flickNorm.X, flickNorm.Y, ReportPeriodSec);
            _flickAccum = Math.Clamp(_flickAccum + deg, -90, 90) * 0.86;
            double offset = Math.Clamp(_flickAccum / FlickStickProcessor.FullDeflection, -1, 1);
            mapped = mapped with
            {
                RX = (sbyte)Math.Clamp(mapped.RX + (int)Math.Round(offset * 127), -127, 127),
            };
        }
        else
        {
            _flickAccum = 0;
        }

        if (!mapped.Equals(_last))
        {
            _pad?.Update(mapped);
            _last = mapped;
        }
    }

    private void ApplyGyro(ref Xbox360State mapped, ProControllerState state)
    {
        if (_gyro.Mode == GyroMode.Off) return;
        var imu = state.Imu.Span;
        if (imu.Length == 0) return;

        if (_gyro.ActivationButton is { } key && !state.Buttons.HasFlag(key))
        {
            _gyro.Reset();
            return;
        }

        if (_gyro.Mode == GyroMode.RightStick)
        {
            var (dx, dy) = _gyro.UpdateRightStick(imu);
            mapped = mapped with
            {
                RX = (sbyte)Math.Clamp(mapped.RX + (int)Math.Round(dx * 127), -127, 127),
                RY = (sbyte)Math.Clamp(mapped.RY + (int)Math.Round(dy * 127), -127, 127),
            };
        }
        else if (_gyro.Mode == GyroMode.Mouse)
        {
            var (mdx, mdy) = _gyro.UpdateMouse(imu);
            if (mdx != 0 || mdy != 0)
                MouseMover.MoveRelative((int)Math.Round(mdx), (int)Math.Round(mdy));
        }
    }

    public void Dispose()
    {
        _engine.Devices.ControllerArrived -= OnSessionsChanged;
        _engine.Devices.ControllerRemoved -= OnSessionsChanged;
        _engine.ActiveProfileChanged -= OnProfileChanged;
        lock (_stateLock)
        {
            StopLocked();
        }
    }
}
