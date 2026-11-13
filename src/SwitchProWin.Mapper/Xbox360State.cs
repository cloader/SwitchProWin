using SwitchProWin.Protocol;

namespace SwitchProWin.Mapper;

/// <summary>Xbox 360 手柄按键（虚拟手柄侧）。扳机作为"按键位"参与映射表。</summary>
[Flags]
public enum XboxButtons : uint
{
    None = 0,
    DpadUp = 1u << 0,
    DpadDown = 1u << 1,
    DpadLeft = 1u << 2,
    DpadRight = 1u << 3,
    Start = 1u << 4,
    Back = 1u << 5,
    LeftThumb = 1u << 6,
    RightThumb = 1u << 7,
    LeftShoulder = 1u << 8,
    RightShoulder = 1u << 9,
    Guide = 1u << 10, // XBOX 徽标键
    A = 1u << 12,
    B = 1u << 13,
    X = 1u << 14,
    Y = 1u << 15,
    LeftTrigger = 1u << 16,  // 仅用于映射表；报告里走 0..255 滑杆
    RightTrigger = 1u << 17,
}

/// <summary>映射后的虚拟手柄一帧状态（XInput 语义）。</summary>
public readonly record struct Xbox360State
{
    public XboxButtons Buttons { get; init; }
    public byte LeftTrigger { get; init; }  // 0..255
    public byte RightTrigger { get; init; } // 0..255
    public sbyte LX { get; init; } // -127..127
    public sbyte LY { get; init; }
    public sbyte RX { get; init; }
    public sbyte RY { get; init; }

    public static Xbox360State Empty => default;
}

/// <summary>按键映射方向：Pro 手柄按键 -> Xbox 按键。</summary>
public static class DefaultButtonMap
{
    /// <summary>生成默认 Nintendo 布局映射（A 在右下、B 在左下，与 Xbox 相反，因此 A/B、X/Y 互换）。</summary>
    public static Dictionary<ProButtons, XboxButtons> CreateNintendoLayout() => new()
    {
        [ProButtons.A] = XboxButtons.B,
        [ProButtons.B] = XboxButtons.A,
        [ProButtons.X] = XboxButtons.Y,
        [ProButtons.Y] = XboxButtons.X,
        [ProButtons.L] = XboxButtons.LeftShoulder,
        [ProButtons.R] = XboxButtons.RightShoulder,
        [ProButtons.Zl] = XboxButtons.LeftTrigger,
        [ProButtons.Zr] = XboxButtons.RightTrigger,
        [ProButtons.Minus] = XboxButtons.Back,
        [ProButtons.Plus] = XboxButtons.Start,
        [ProButtons.LStickPress] = XboxButtons.LeftThumb,
        [ProButtons.RStickPress] = XboxButtons.RightThumb,
        [ProButtons.Home] = XboxButtons.Guide,
        [ProButtons.Up] = XboxButtons.DpadUp,
        [ProButtons.Down] = XboxButtons.DpadDown,
        [ProButtons.Left] = XboxButtons.DpadLeft,
        [ProButtons.Right] = XboxButtons.DpadRight,
    };

    /// <summary>生成 Xbox 布局映射（不互换 A/B、X/Y），适合习惯 Xbox 键位标示的用户。</summary>
    public static Dictionary<ProButtons, XboxButtons> CreateXboxLayout() => new()
    {
        [ProButtons.A] = XboxButtons.A,
        [ProButtons.B] = XboxButtons.B,
        [ProButtons.X] = XboxButtons.X,
        [ProButtons.Y] = XboxButtons.Y,
        [ProButtons.L] = XboxButtons.LeftShoulder,
        [ProButtons.R] = XboxButtons.RightShoulder,
        [ProButtons.Zl] = XboxButtons.LeftTrigger,
        [ProButtons.Zr] = XboxButtons.RightTrigger,
        [ProButtons.Minus] = XboxButtons.Back,
        [ProButtons.Plus] = XboxButtons.Start,
        [ProButtons.LStickPress] = XboxButtons.LeftThumb,
        [ProButtons.RStickPress] = XboxButtons.RightThumb,
        [ProButtons.Home] = XboxButtons.Guide,
        [ProButtons.Up] = XboxButtons.DpadUp,
        [ProButtons.Down] = XboxButtons.DpadDown,
        [ProButtons.Left] = XboxButtons.DpadLeft,
        [ProButtons.Right] = XboxButtons.DpadRight,
    };

    // 兼容旧代码的静态表（Nintendo 布局）
    public static readonly Dictionary<ProButtons, XboxButtons> NintendoLayout = CreateNintendoLayout();
    public static readonly Dictionary<ProButtons, XboxButtons> XboxLayout = CreateXboxLayout();
}
