using HidSharp;

namespace SwitchProWin.Engine;

/// <summary>
/// 设备监视器：枚举 + 热插拔发现 Switch Pro 手柄，为每只手柄创建会话。
/// 覆盖三类断线场景：读错误（Closed 事件）、设备消失（Changed 事件）、
/// 事件丢失（2 秒周期重扫兜底）。
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private readonly Dictionary<string, ProControllerSession> _sessions = new();
    private readonly object _lock = new();
    // 扫描串行化：Start 含阻塞 IO（SPI 应答等待可达秒级），不能持有 _lock，
    // 否则 UI 的 Sessions 查询会被卡死；用信号量保证扫描互不重入
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly System.Threading.Timer _rescanTimer;
    private bool _running;

    public event Action<ProControllerSession>? ControllerArrived;
    public event Action<ProControllerSession>? ControllerRemoved;
    public event Action<string>? Log;

    /// <summary>
    /// 会话配置钩子：每个新手柄 Start 成功后立即调用（应用映射配置/订阅事件）。
    /// </summary>
    public Action<ProControllerSession>? ConfigureSession { get; set; }

    /// <summary>
    /// 是否为该种类创建会话级虚拟手柄（Joy-Con 配对模式返回 false，由合并器统一输出）。
    /// </summary>
    public Func<ControllerKind, bool> ShouldCreatePad { get; set; } = _ => true;

    /// <summary>当前会话快照。</summary>
    public IReadOnlyList<ProControllerSession> Sessions
    {
        get { lock (_lock) return _sessions.Values.ToList(); }
    }

    public DeviceManager()
    {
        // 兜底重扫：蓝牙手柄休眠/恢复、驱动重载等场景 Changed 事件可能不可靠
        _rescanTimer = new System.Threading.Timer(_ =>
        {
            if (_running) ScanAndConnect();
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Start()
    {
        _running = true;
        DeviceList.Local.Changed += OnDeviceListChanged;
        ScanAndConnect();
        Log?.Invoke(Loc.S("log.deviceWatchStarted"));
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        if (!_running) return;
        ScanAndConnect();
    }

    private void ScanAndConnect()
    {
        try
        {
            if (!_scanGate.Wait(0))
                return; // 已有扫描在进行：2 秒兜底重扫会补上
        }
        catch (ObjectDisposedException)
        {
            return; // Dispose 竞态：已停止
        }
        try
        {
            ScanCore();
        }
        finally
        {
            try { _scanGate.Release(); }
            catch (ObjectDisposedException) { /* Dispose 竞态 */ }
        }
    }

    private void ScanCore()
    {
        var devices = DeviceList.Local.GetHidDevices(vendorID: HardwareIds.NintendoVendorId)
            .Where(d => HardwareIds.IsSupported(d.VendorID, d.ProductID))
            .ToList();

        var removedEvents = new List<ProControllerSession>();

        // ---- 移除消失的（短锁，事件锁外触发） ----
        lock (_lock)
        {
            foreach (var path in _sessions.Keys.ToList())
            {
                if (devices.All(d => !string.Equals(d.DevicePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    var session = _sessions[path];
                    _sessions.Remove(path);
                    removedEvents.Add(session);
                }
            }
        }
        foreach (var session in removedEvents)
        {
            session.Dispose();
            Log?.Invoke(Loc.F("log.deviceRemoved", ShortPath(session.DevicePath)));
            ControllerRemoved?.Invoke(session);
        }

        // ---- 接入新的（Start 在锁外执行：含阻塞 IO） ----
        var toAdd = devices
            .Where(d => { lock (_lock) return !_sessions.ContainsKey(d.DevicePath); })
            .ToList();

        foreach (var device in toAdd)
        {
            if (!_running) return; // Dispose 进行中：不再接入新设备

            lock (_lock)
            {
                if (_sessions.ContainsKey(device.DevicePath)) continue; // 并发扫描已接入
            }

            // 分配 0..3 中最小空闲玩家号（用 _sessions.Count 会在"移除后再接入"时撞号：
            // 两只手柄同号 → LED 重叠、低电量提醒互相抑制）
            int nextIndex;
            lock (_lock)
            {
                var used = new HashSet<int>();
                foreach (var s in _sessions.Values) used.Add(s.PlayerIndex);
                nextIndex = Enumerable.Range(0, 4).FirstOrDefault(i => !used.Contains(i));
            }

            var pidHint = HardwareIds.KindFromPid(device.ProductID);
            Log?.Invoke(Loc.F("log.deviceFound", device.GetProductName() ?? "Controller", device.ProductID, ShortPath(device.DevicePath)));
            var session = new ProControllerSession(device, pidHint);
            // 会话日志统一转发（必须在 Start 前订阅，否则初始化日志丢失）
            session.Log += m => Log?.Invoke($"[{SessionLabel(session)}] {m}");
            // 读循环异常终止（拔出/链路断开）→ 异步清理，避免在读线程上自等待
            session.Closed += s => System.Threading.Tasks.Task.Run(() => RemoveSession(s, "连接断开"));

            // 握把（PID 200E）在 Start 内经 SPI 判别 L/R；已知 PID 的创建决策在此，
            // 握把先按"创建"启动，判别出 Joy-Con 且配对模式时撤回（由合并器输出）
            bool createPad = pidHint is null || ShouldCreatePad(pidHint.Value);
            if (session.Start(nextIndex, createPad))
            {
                if (createPad && session.IsJoyCon && !ShouldCreatePad(session.Kind))
                {
                    // 握把判别为 Joy-Con 且处于配对模式：撤回本会话的虚拟手柄
                    session.WithdrawVirtualPad();
                }
                lock (_lock) _sessions[device.DevicePath] = session;
                try { ConfigureSession?.Invoke(session); }
                catch (Exception ex) { Log?.Invoke(Loc.F("log.sessionConfigFailed", ex.Message)); }
                ControllerArrived?.Invoke(session);
            }
            else
            {
                session.Dispose(); // 释放 replyEvent 等句柄，避免泄漏
            }
        }
    }

    private static string SessionLabel(ProControllerSession s) => s.Kind switch
    {
        ControllerKind.JoyConLeft => "Joy-Con L",
        ControllerKind.JoyConRight => "Joy-Con R",
        _ => "手柄",
    };

    /// <summary>重建全部会话（模式切换用：Joy-Con 配对/单只改变虚拟手柄拓扑）。</summary>
    public void RestartSessions()
    {
        List<ProControllerSession> disposed;
        lock (_lock)
        {
            disposed = _sessions.Values.ToList();
            _sessions.Clear();
        }
        foreach (var s in disposed) s.Dispose();
        if (_running) ScanAndConnect();
    }

    private void RemoveSession(ProControllerSession session, string reason)
    {
        bool removed;
        lock (_lock)
        {
            removed = _sessions.Remove(session.DevicePath);
        }
        if (!removed) return; // 已被清理（如 Dispose 扫描）

        Log?.Invoke(Loc.F("log.deviceOffline", reason));
        session.Dispose();
        ControllerRemoved?.Invoke(session);

        // 立即重扫：蓝牙瞬断场景下设备若仍在线，马上重建会话（不等 2 秒兜底）
        if (_running) ScanAndConnect();
    }

    private static string ShortPath(string path)
    {
        // 提取 &nnnnnn 形式的实例编号片段方便辨认
        var parts = path.Split('#');
        return parts.Length >= 3 ? $"{parts[0]}#{parts[1]}#{parts[2]}" : path;
    }

    public void Dispose()
    {
        _running = false;
        _rescanTimer.Dispose();
        DeviceList.Local.Changed -= OnDeviceListChanged;
        lock (_lock)
        {
            foreach (var s in _sessions.Values)
                s.Dispose();
            _sessions.Clear();
        }
        _scanGate.Dispose();
    }
}
