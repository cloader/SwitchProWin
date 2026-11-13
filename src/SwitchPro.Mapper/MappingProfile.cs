using SwitchPro.Protocol;

namespace SwitchPro.Mapper;

/// <summary>摇杆响应曲线类型。</summary>
public enum StickCurve
{
    Linear,
    GentleQuad,  // 幂 1.4
    Exponential, // 幂 2.0
    AggressiveExponential, // 幂 3.0
    Cubic,       // x^3 多项式（DS4Windows 风格）
}

/// <summary>摇杆处理参数：死区（内/外，比例 0..1）、圆形化、响应曲线。</summary>
public sealed class StickSettings
{
    public double InnerDeadzone { get; set; } = 0.10;
    public double OuterDeadzone { get; set; } = 0.04;
    public StickCurve Curve { get; set; } = StickCurve.GentleQuad;

    /// <summary>把 -1..1 的轴向分量做圆形化 + 死区 + 曲线处理（径向处理，保留方向）。</summary>
    public (double x, double y) Apply(double x, double y)
    {
        double len = Math.Sqrt(x * x + y * y);
        if (len <= InnerDeadzone || len < 1e-9)
            return (0, 0);

        // 内死区归零、外死区满幅
        double t = (len - InnerDeadzone) / Math.Max(1e-9, 1.0 - InnerDeadzone - OuterDeadzone);
        t = Math.Clamp(t, 0.0, 1.0);
        t = ApplyCurve(t);

        double scale = t / len;
        return (Math.Clamp(x * scale, -1.0, 1.0), Math.Clamp(y * scale, -1.0, 1.0));
    }

    private double ApplyCurve(double t) => Curve switch
    {
        StickCurve.Linear => t,
        StickCurve.GentleQuad => Math.Pow(t, 1.4),
        StickCurve.Exponential => t * t,
        StickCurve.AggressiveExponential => t * t * t,
        StickCurve.Cubic => t * t * t * (3.0 - 2.0 * t), // smoothstep：中段增益略高
        _ => t,
    };
}

/// <summary>整体映射配置。</summary>
public sealed class MappingProfile
{
    /// <summary>
    /// 自定义按键映射（Pro → Xbox）。非 null 时优先于 NintendoLayout 预设；
    /// ZL/ZR 始终映射为扳机，不经过此表。
    /// </summary>
    public Dictionary<ProButtons, XboxButtons>? ButtonMap { get; set; }

    public bool NintendoLayout { get; set; } = true; // A/B、X/Y 互换
    public StickSettings LeftStick { get; } = new();
    public StickSettings RightStick { get; } = new();

    /// <summary>摇杆校准（由 Engine 从手柄 SPI 读到后注入；未读到时用默认值）。</summary>
    public StickCalibration LeftStickCalibration { get; set; } = StickCalibration.Default;
    public StickCalibration RightStickCalibration { get; set; } = StickCalibration.Default;

    /// <summary>把 Pro 手柄状态映射为 Xbox 360 状态。</summary>
    public Xbox360State Map(ProControllerState input)
    {
        var table = ButtonMap ??
            (NintendoLayout ? DefaultButtonMap.NintendoLayout : DefaultButtonMap.XboxLayout);

        var buttons = XboxButtons.None;
        byte lt = 0, rt = 0;
        foreach (var (pro, xbox) in table)
        {
            if (xbox == XboxButtons.None || !input.Buttons.HasFlag(pro))
                continue;
            switch (xbox)
            {
                case XboxButtons.LeftTrigger: lt = 255; break;
                case XboxButtons.RightTrigger: rt = 255; break;
                default: buttons |= xbox; break;
            }
        }

        var (lx, ly) = NormalizeLeftStick(input.LeftStick);
        var (rx, ry) = NormalizeRightStick(input.RightStick);

        return new Xbox360State
        {
            Buttons = buttons,
            LeftTrigger = lt,
            RightTrigger = rt,
            LX = ToSbyte(lx),
            LY = ToSbyte(ly), // ViGEm X360 Y 轴：正值=上（实测 winmm 验证），摇杆上推=正
            RX = ToSbyte(rx),
            RY = ToSbyte(ry),
        };
    }

    /// <summary>按校准 + 死区 + 曲线归一化左摇杆，返回 (-1..1, -1..1)，Y 轴向上为正。</summary>
    public (double X, double Y) NormalizeLeftStick(RawStick raw)
        => NormalizeStick(raw, LeftStickCalibration, LeftStick);

    /// <summary>按校准 + 死区 + 曲线归一化右摇杆，返回 (-1..1, -1..1)，Y 轴向上为正。</summary>
    public (double X, double Y) NormalizeRightStick(RawStick raw)
        => NormalizeStick(raw, RightStickCalibration, RightStick);

    private static (double x, double y) NormalizeStick(RawStick raw, StickCalibration cal, StickSettings settings)
    {
        // 先按校准中心归零，再按上下行量程分别归一化（-1..1）
        double x = raw.X >= cal.CenterX
            ? (raw.X - cal.CenterX) / (double)Math.Max(1, cal.MaxX - cal.CenterX)
            : (raw.X - cal.CenterX) / (double)Math.Max(1, cal.CenterX - cal.MinX);
        double y = raw.Y >= cal.CenterY
            ? (raw.Y - cal.CenterY) / (double)Math.Max(1, cal.MaxY - cal.CenterY)
            : (raw.Y - cal.CenterY) / (double)Math.Max(1, cal.CenterY - cal.MinY);

        x = Math.Clamp(x, -1.0, 1.0);
        y = Math.Clamp(y, -1.0, 1.0);
        return settings.Apply(x, y);
    }

    private static sbyte ToSbyte(double v)
        => (sbyte)Math.Round(Math.Clamp(v, -1.0, 1.0) * 127.0);
}
