using SwitchPro.Mapper;
using SwitchPro.Protocol;

namespace SwitchPro.Engine;

/// <summary>
/// 一份控制器配置文件：按键映射表 + 摇杆参数 + 震动强度。
/// 按键表以 Xbox 目标键为槽位、Pro 物理键为绑定值（ZL/ZR 固定为扳机，不入表）。
/// </summary>
public sealed class ControllerProfile
{
    public string Name { get; set; } = "默认";

    public Dictionary<ProButtons, XboxButtons> ButtonMap { get; set; } =
        DefaultButtonMap.CreateNintendoLayout();

    public double LeftDeadzone { get; set; } = 0.10;
    public double RightDeadzone { get; set; } = 0.10;
    public double LeftOuterDeadzone { get; set; } = 0.04;
    public double RightOuterDeadzone { get; set; } = 0.04;
    public StickCurve LeftCurve { get; set; } = StickCurve.GentleQuad;
    public StickCurve RightCurve { get; set; } = StickCurve.GentleQuad;

    /// <summary>震动强度上限（0..1，相对安全最大幅度）。</summary>
    public float RumbleMaxAmplitude { get; set; } = RumbleEncoder.DefaultMaxAmplitude;

    // ---- 摇杆软件校准（向导产物；null = 使用手柄 SPI 出厂/用户校准） ----

    public StickCalibration? LeftStickCalibrationOverride { get; set; }
    public StickCalibration? RightStickCalibrationOverride { get; set; }

    // ---- 体感 ----

    public GyroMode GyroMode { get; set; } = GyroMode.Off;
    public double GyroSensitivity { get; set; } = 1.0;
    public bool GyroInvertX { get; set; }
    public bool GyroInvertY { get; set; }
    public bool GyroSwapAxes { get; set; }

    /// <summary>按住此键时体感才生效；空 = 始终启用。存 ProButtons 名称。</summary>
    public string? GyroActivationButton { get; set; }

    /// <summary>静止自动重校零（默认开）。</summary>
    public bool GyroAutoCalibrate { get; set; } = true;

    /// <summary>
    /// 自动切换进程列表（小写进程名、不含 .exe）。这些程序位于前台时自动激活本配置。
    /// </summary>
    public List<string> AutoSwitchProcesses { get; set; } = new();

    /// <summary>连发按键（ProButtons 名称）：按住时以 ~10Hz 通断。</summary>
    public List<string> TurboButtons { get; set; } = new();

    /// <summary>Flick Stick（甩杆瞄准）：右摇杆改为指向转向，配合陀螺仪微调。</summary>
    public bool FlickStickEnabled { get; set; }

    /// <summary>Flick Stick 灵敏度倍率（0.5-2.0）。</summary>
    public double FlickStickSensitivity { get; set; } = 1.0;

    /// <summary>闲置自动休眠（分钟，0 = 关闭）。</summary>
    public int IdleSleepMinutes { get; set; }

    /// <summary>解析激活键（无效/空返回 null = 始终启用）。</summary>
    public ProButtons? ResolveGyroActivation()
        => Enum.TryParse<ProButtons>(GyroActivationButton, out var b) && b != ProButtons.None ? b : null;

    /// <summary>把本配置应用到会话的映射模板。</summary>
    public void ApplyTo(MappingProfile profile)
    {
        profile.ButtonMap = new Dictionary<ProButtons, XboxButtons>(ButtonMap);
        profile.LeftStick.InnerDeadzone = LeftDeadzone;
        profile.LeftStick.OuterDeadzone = LeftOuterDeadzone;
        profile.LeftStick.Curve = LeftCurve;
        profile.RightStick.InnerDeadzone = RightDeadzone;
        profile.RightStick.OuterDeadzone = RightOuterDeadzone;
        profile.RightStick.Curve = RightCurve;

        // 软件校准覆盖（校准向导产物）；为空则保留会话 SPI 读到的校准
        if (LeftStickCalibrationOverride is { } l)
            profile.LeftStickCalibration = l;
        if (RightStickCalibrationOverride is { } r)
            profile.RightStickCalibration = r;
    }

    /// <summary>把 Nintendo/Xbox 预设写回按键表（捕获修改前的快捷重置）。</summary>
    public void UseLayoutPreset(bool nintendo)
        => ButtonMap = nintendo ? DefaultButtonMap.CreateNintendoLayout() : DefaultButtonMap.CreateXboxLayout();

    /// <summary>某 Xbox 槽位当前绑定的 Pro 键（未绑定为 null）。
    /// 注意不能用 FirstOrDefault().Key 判空——Key 是非空枚举，default 就是 None。</summary>
    public ProButtons? BindingOf(XboxButtons target)
    {
        foreach (var kv in ButtonMap)
            if (kv.Value == target)
                return kv.Key;
        return null;
    }

    /// <summary>设置槽位绑定（value=null 解绑）。</summary>
    public void Bind(XboxButtons target, ProButtons? pro)
    {
        foreach (var key in ButtonMap.Where(kv => kv.Value == target).Select(kv => kv.Key).ToList())
            ButtonMap.Remove(key);
        if (pro is { } p)
            ButtonMap[p] = target;
    }
}
