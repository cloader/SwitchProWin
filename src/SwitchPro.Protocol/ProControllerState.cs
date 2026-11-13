namespace SwitchPro.Protocol;

/// <summary>Switch Pro 手柄上的全部按键（标准全量模式 0x30 的按钮位）。</summary>
[Flags]
public enum ProButtons : uint
{
    None = 0,

    // Byte 3 (Right group)
    Y = 1u << 0,
    X = 1u << 1,
    B = 1u << 2,
    A = 1u << 3,
    SrRight = 1u << 4,
    SlRight = 1u << 5,
    R = 1u << 6,
    Zr = 1u << 7,

    // Byte 4 (Shared)
    Minus = 1u << 8,
    Plus = 1u << 9,
    RStickPress = 1u << 10,
    LStickPress = 1u << 11,
    Home = 1u << 12,
    Capture = 1u << 13,
    ChargingGrip = 1u << 15,

    // Byte 5 (Left group)
    Down = 1u << 16,
    Up = 1u << 17,
    Right = 1u << 18,
    Left = 1u << 19,
    SrLeft = 1u << 20,
    SlLeft = 1u << 21,
    L = 1u << 22,
    Zl = 1u << 23,
}

/// <summary>摇杆原始 12 位读数（0..4095，中心值取决于手柄校准数据）。</summary>
public readonly record struct RawStick(ushort X, ushort Y);

/// <summary>一组 IMU 读数（加速度单位 g，角速度单位 度/秒）。</summary>
public readonly record struct ImuSample(
    float AccX, float AccY, float AccZ,
    float GyrX, float GyrY, float GyrZ);

/// <summary>一次输入报告解析出的手柄完整状态。</summary>
public readonly record struct ProControllerState
{
    /// <summary>报告计数器（0x30/0x21 的 byte1），用于丢包诊断。</summary>
    public byte Timer { get; init; }

    /// <summary>电量 0..8（8=满电）。</summary>
    public int BatteryLevel { get; init; }

    public bool Charging { get; init; }

    /// <summary>连接信息位指示的设备类型：true = Joy-Con（位值 3），false = Pro/充电握把。
    /// 注意这是"设备种类"而非传输方式（握把中的 Joy-Con 走 USB 也为 true）；
    /// 传输方式请用会话级 IsBluetooth（按设备路径判别）。</summary>
    public bool IsJoyConType { get; init; }

    public ProButtons Buttons { get; init; }

    public RawStick LeftStick { get; init; }

    public RawStick RightStick { get; init; }

    /// <summary>全量模式下的 3 帧 IMU 样本；简单模式下为空。</summary>
    public ReadOnlyMemory<ImuSample> Imu { get; init; }

    public static ProControllerState Empty => default;
}
