using SwitchProWin.Mapper;
using SwitchProWin.Protocol;

namespace SwitchProWin.Engine;

/// <summary>
/// 引擎编排：配置加载/热应用 + 设备管理 + 活动配置文件管理。
/// UI（WPF/托盘）与 CLI 都经由本类驱动引擎。
/// </summary>
public sealed class SwitchProWinEngine : IDisposable
{
    public AppConfig Config { get; }

    public DeviceManager Devices { get; }

    /// <summary>引擎/设备日志（含会话日志）。</summary>
    public event Action<string>? Log;

    /// <summary>活动配置文件被修改（UI 刷新用）。</summary>
    public event Action? ActiveProfileChanged;

    // 环形日志缓冲：UI 订阅晚于引擎启动时回放历史（启动早于窗口构造）
    private readonly object _logBufferLock = new();
    private readonly List<string> _logBuffer = new();
    private const int LogBufferCapacity = 500;

    // 按游戏自动切配置：前台进程监控
    private readonly System.Threading.Timer _foregroundTimer;

    // 低电量提醒：每 5 秒检查
    private readonly System.Threading.Timer _batteryTimer;
    private readonly HashSet<int> _lowBatteryWarned = new();
    public const int LowBatteryThreshold = 1; // ≤1/8 且未充电时提醒

    /// <summary>低电量警告（参数：玩家编号），App 层转托盘气泡。</summary>
    public event Action<int>? LowBatteryWarning;

    // Joy-Con 配对合并器
    private JoyConPairManager? _pairManager;

    public SwitchProWinEngine()
    {
        Config = AppConfig.Load();
        Devices = new DeviceManager();
        Devices.Log += OnLog;
        // 新手柄接入即应用当前配置
        Devices.ConfigureSession = ApplyConfig;
        // 配对模式下 Joy-Con 不各自创建虚拟手柄（由合并器统一输出）
        Devices.ShouldCreatePad = kind =>
            !(Config.JoyConMode == JoyConMode.Pair
              && kind is ControllerKind.JoyConLeft or ControllerKind.JoyConRight);

        _pairManager = new JoyConPairManager(this, m => OnLog(m));

        _foregroundTimer = new System.Threading.Timer(_ => AutoSwitchTick(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(800));

        _batteryTimer = new System.Threading.Timer(_ => BatteryTick(), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

        // 组合键宏：订阅全部会话的按键流
        Devices.ConfigureSession += HookComboDetector;
    }

    // ---- Joy-Con 模式 ----

    /// <summary>切换 Joy-Con 模式并重建会话（虚拟手柄拓扑变化）。
    /// 重建含每只手柄秒级 SPI 等待，放后台执行避免卡 UI。</summary>
    public void SetJoyConMode(JoyConMode mode)
    {
        if (Config.JoyConMode == mode) return;
        Config.JoyConMode = mode;
        try { Config.Save(); }
        catch (Exception ex) { OnLog(Loc.F("log.saveFailed", ex.Message)); }
        OnLog(mode == JoyConMode.Pair ? Loc.S("log.jcPair") : Loc.S("log.jcSingle"));
        Task.Run(() => Devices.RestartSessions()); // 重建后 ControllerArrived → 合并器自动重算
    }

    // ---- 组合键宏 ----

    private void HookComboDetector(ProControllerSession session)
    {
        session.StateReceived += state => DetectCombo(session, state.Buttons);
        // 会话断开：清理按键历史（防字典无限增长/持有已释放会话）
        session.Closed += s => { lock (_comboLock) _comboLast.Remove(s); };
    }

    private readonly object _comboLock = new();
    private readonly Dictionary<ProControllerSession, ProButtons> _comboLast = new();

    /// <summary>按键沿检测在读线程执行（每帧）：仅做位运算判断，动作转发线程池。</summary>
    private void DetectCombo(ProControllerSession session, ProButtons buttons)
    {
        ProButtons prev, newlyPressed;
        lock (_comboLock)
        {
            _comboLast.TryGetValue(session, out prev);
            _comboLast[session] = buttons;
        }
        newlyPressed = buttons & ~prev;
        if (newlyPressed == 0) return;

        const ProButtons baseCombo = ProButtons.L | ProButtons.R;
        if ((buttons & baseCombo) != baseCombo) return;

        if (Config.ComboProfileCycle && newlyPressed.HasFlag(ProButtons.Minus))
        {
            Task.Run(CycleActiveProfile); // 动作含文件 IO/事件广播，不占读线程
        }
        else if (Config.ComboGyroRecenter && newlyPressed.HasFlag(ProButtons.Plus))
        {
            Task.Run(() =>
            {
                foreach (var s in Devices.Sessions)
                    s.CalibrateGyroNow();
                _pairManager?.RecenterGyro();
                OnLog(Loc.S("log.comboRecenter"));
            });
        }
    }

    /// <summary>循环切换到下一个配置文件。</summary>
    public void CycleActiveProfile()
    {
        List<string> names;
        lock (_configLock)
        {
            Config.EnsureProfiles();
            names = Config.Profiles.Select(p => p.Name).ToList();
        }
        if (names.Count <= 1) return;
        int idx = names.IndexOf(Config.ActiveProfileName);
        var next = names[(idx + 1) % names.Count];
        OnLog(Loc.F("log.comboProfile", next));
        SetActiveProfile(next);
    }

    private void BatteryTick()
    {
        try
        {
            foreach (var session in Devices.Sessions)
            {
                var st = session.LatestState;
                if (st is not { } s) continue;
                bool low = s.BatteryLevel <= LowBatteryThreshold && !s.Charging;
                if (low && _lowBatteryWarned.Add(session.PlayerIndex))
                    LowBatteryWarning?.Invoke(session.PlayerIndex + 1);
                else if (!low)
                    _lowBatteryWarned.Remove(session.PlayerIndex);
            }
        }
        catch
        {
            // 检查失败静默
        }
    }

    /// <summary>前台进程命中某配置的自动切换列表时切换过去；离开时切回默认行为：保持不变。</summary>
    private void AutoSwitchTick()
    {
        try
        {
            var process = ForegroundProcess.Name();
            if (process is null || process == "switchproapp") return;

            ControllerProfile? target;
            lock (_configLock) // 快照读取，避免与 UI 增删配置竞态
            {
                target = Config.Profiles.FirstOrDefault(p =>
                    p.AutoSwitchProcesses.Contains(process, StringComparer.OrdinalIgnoreCase));
            }
            if (target is not null && target.Name != Config.ActiveProfileName)
            {
                OnLog(Loc.F("log.autoSwitched", process, target.Name));
                SetActiveProfile(target.Name);
            }
        }
        catch
        {
            // 监控失败静默跳过（进程退出竞态等）
        }
    }

    public void Start()
    {
        Devices.Start();
    }

    private void OnLog(string message)
    {
        lock (_logBufferLock)
        {
            _logBuffer.Add(message);
            if (_logBuffer.Count > LogBufferCapacity)
                _logBuffer.RemoveRange(0, _logBuffer.Count - LogBufferCapacity);
        }
        Log?.Invoke(message);
    }

    /// <summary>缓冲的历史日志快照（最早在前）。</summary>
    public IReadOnlyList<string> RecentLogs
    {
        get
        {
            lock (_logBufferLock)
                return _logBuffer.ToList();
        }
    }

    /// <summary>配置列表互斥锁：UI 增删配置与组合键任务/定时器读取并发访问。</summary>
    private readonly object _configLock = new();

    /// <summary>当前活动配置文件。</summary>
    public ControllerProfile ActiveProfile
    {
        get { lock (_configLock) return Config.GetActive(); }
    }

    /// <summary>把手柄会话应用活动配置（新手柄接入与配置修改共用）。</summary>
    public void ApplyConfig(ProControllerSession session)
    {
        var profile = ActiveProfile;
        profile.ApplyTo(session.Profile);
        session.RumbleMaxAmplitude = profile.RumbleMaxAmplitude;

        var gyro = session.Gyro;
        // 配对模式下的 Joy-Con：体感由合并器统一处理（用 L 的 IMU），会话自身关闭体感
        bool pairSuppressed = Config.JoyConMode == JoyConMode.Pair && session.IsJoyCon;
        gyro.Mode = pairSuppressed ? GyroMode.Off : profile.GyroMode;
        gyro.Sensitivity = profile.GyroSensitivity;
        gyro.InvertX = profile.GyroInvertX;
        gyro.InvertY = profile.GyroInvertY;
        gyro.SwapAxes = profile.GyroSwapAxes;
        gyro.ActivationButton = profile.ResolveGyroActivation();
        gyro.AutoCalibrate = profile.GyroAutoCalibrate;
        if (gyro.Mode == GyroMode.Off)
            gyro.Reset();
        else
            session.MarkGyroNeedsCalibration(); // 参数变化后重新静止校零

        // Turbo / 闲置休眠 / Flick Stick
        session.TurboSet.Clear();
        foreach (var name in profile.TurboButtons)
            if (Enum.TryParse<ProButtons>(name, out var b) && b != ProButtons.None)
                session.TurboSet.Add(b);
        session.RefreshTurboMask();
        session.IdleSleepMs = profile.IdleSleepMinutes * 60_000L;
        session.FlickStickEnabled = profile.FlickStickEnabled;
        session.FlickStick.Sensitivity = profile.FlickStickSensitivity;
    }

    /// <summary>保存配置并对所有在线手柄热生效。</summary>
    public void ApplyAndSave()
    {
        try { Config.Save(); }
        catch (Exception ex) { Log?.Invoke(Loc.F("log.saveFailed", ex.Message)); }
        ApplyConfigToAll();
        ActiveProfileChanged?.Invoke();
    }

    /// <summary>对在线手柄热生效当前活动配置（不落盘）。</summary>
    public void ApplyConfigToAll()
    {
        foreach (var session in Devices.Sessions)
        {
            try
            {
                ApplyConfig(session);
            }
            catch (Exception ex)
            {
                Log?.Invoke(Loc.F("log.applyFailed", ex.Message));
            }
        }
    }

    // ---- 配置文件管理 ----

    public IEnumerable<ControllerProfile> Profiles
    {
        get { lock (_configLock) return Config.Profiles.ToList(); }
    }

    public void SetActiveProfile(string name)
    {
        lock (_configLock)
        {
            Config.ActiveProfileName = name;
            ApplyAndSave();
        }
    }

    public ControllerProfile AddProfile(string name)
    {
        var profile = new ControllerProfile { Name = name };
        lock (_configLock)
        {
            // 新配置完整继承当前活动配置
            var src = ActiveProfile; // 同线程已持锁，可重入
            profile.ButtonMap = new Dictionary<ProButtons, XboxButtons>(src.ButtonMap);
            profile.LeftDeadzone = src.LeftDeadzone;
            profile.RightDeadzone = src.RightDeadzone;
            profile.LeftOuterDeadzone = src.LeftOuterDeadzone;
            profile.RightOuterDeadzone = src.RightOuterDeadzone;
            profile.LeftCurve = src.LeftCurve;
            profile.RightCurve = src.RightCurve;
            profile.RumbleMaxAmplitude = src.RumbleMaxAmplitude;
            profile.LeftStickCalibrationOverride = src.LeftStickCalibrationOverride;
            profile.RightStickCalibrationOverride = src.RightStickCalibrationOverride;
            profile.GyroMode = src.GyroMode;
            profile.GyroSensitivity = src.GyroSensitivity;
            profile.GyroInvertX = src.GyroInvertX;
            profile.GyroInvertY = src.GyroInvertY;
            profile.GyroSwapAxes = src.GyroSwapAxes;
            profile.GyroActivationButton = src.GyroActivationButton;
            profile.GyroAutoCalibrate = src.GyroAutoCalibrate;
            profile.AutoSwitchProcesses = new List<string>(src.AutoSwitchProcesses);
            profile.TurboButtons = new List<string>(src.TurboButtons);
            profile.FlickStickEnabled = src.FlickStickEnabled;
            profile.FlickStickSensitivity = src.FlickStickSensitivity;
            profile.IdleSleepMinutes = src.IdleSleepMinutes;

            Config.Profiles.Add(profile);
            Config.ActiveProfileName = name;
            ApplyAndSave();
        }
        return profile;
    }

    public bool DeleteProfile(string name)
    {
        lock (_configLock)
        {
            Config.EnsureProfiles();
            if (Config.Profiles.Count <= 1) return false; // 至少保留一份
            var removed = Config.Profiles.RemoveAll(p => p.Name == name) > 0;
            if (!removed) return false;
            if (Config.ActiveProfileName == name)
                Config.ActiveProfileName = Config.Profiles[0].Name;
            ApplyAndSave();
            return true;
        }
    }

    public void Dispose()
    {
        _foregroundTimer.Dispose();
        _batteryTimer.Dispose();
        _pairManager?.Dispose();
        Devices.Dispose();
    }
}
