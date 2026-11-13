namespace SwitchProWin.Protocol;

/// <summary>HID 报告 ID 与输出报告长度常量。</summary>
public static class ReportId
{
    // ---- 输入（手柄 -> 主机） ----
    public const byte SubcommandReply = 0x21; // 标准输入 + 子命令应答
    public const byte StandardFull = 0x30;    // 标准全量模式（含 IMU）
    public const byte SimpleHid = 0x3F;       // 简单 HID 模式（USB 上电默认）

    // ---- 输出（主机 -> 手柄） ----
    public const byte RumbleOnly = 0x10;      // 仅震动
    public const byte RumbleAndSubcommand = 0x01; // 震动 + 子命令
    public const byte RequestNfc = 0x11;

    // ---- 蓝牙握手（M2 使用；USB 上发 0x80 0x04 可阻止超时回蓝牙） ----
    public const byte UartHandshake = 0x80;

    /// <summary>USB HID 输出报告的固定长度（0x40=64 字节，hid_write 全长发送）。</summary>
    public const int OutputReportLength = 0x40;

    /// <summary>0x30 全量输入报告长度。</summary>
    public const int StandardFullInputLength = 0x32; // 50 字节

    /// <summary>0x3F 简单输入报告长度（Pro 手柄：12 字节）。</summary>
    public const int SimpleInputLength = 0x0C;
}

/// <summary>子命令 ID（经 0x01 输出报告发送）。</summary>
public static class Subcommand
{
    public const byte GetBluetoothInfo = 0x01;
    public const byte SetInputReportMode = 0x03;
    public const byte TriggerButtonsElapsedTime = 0x04;
    public const byte SetPlayerLeds = 0x30;
    public const byte GetPlayerLeds = 0x31;
    public const byte SetHomeLed = 0x38;
    public const byte EnableImu = 0x40;
    public const byte SetImuSensitivity = 0x41;
    public const byte EnableVibration = 0x48;
    public const byte GetVibrationInfo = 0x50;
    public const byte SpiRead = 0x10;
    public const byte SpiWrite = 0x11;
    public const byte SetNfcMcuMode = 0x22;
    public const byte SetShutdown = 0x06;
}

/// <summary>输入报告模式（子命令 0x03 的参数）。</summary>
public enum InputMode : byte
{
    StandardFull = 0x30, // 60/120Hz，含 IMU
    NfcIrMode = 0x31,
    SimpleHid = 0x3F,    // 按键变化时上报
}

/// <summary>玩家 LED 掩码（子命令 0x30 的参数位）。</summary>
[Flags]
public enum PlayerLeds : byte
{
    None = 0,
    Led1 = 1 << 0,
    Led2 = 1 << 1,
    Led3 = 1 << 2,
    Led4 = 1 << 3,
}

/// <summary>手柄 SPI Flash 中与摇杆/IMU 校准相关的地址。</summary>
public static class SpiAddress
{
    public const uint LeftStickFactory = 0x603D;  // 9 字节
    public const uint RightStickFactory = 0x6046; // 9 字节
    public const uint LeftStickUser = 0x8010;     // magic(2) + 9 字节
    public const uint RightStickUser = 0x801B;    // magic(2) + 9 字节
    public const uint ImuFactory = 0x6020;        // 24 字节
    public const uint ImuUser = 0x8026;           // magic(2) + 24 字节
    public const uint DeviceType = 0x6012;        // 1 字节：1=JC L, 2=JC R, 3=Pro
    public const ushort UserCalMagic = 0xA1B2;    // 内存中低字节在前：B2 A1
}
