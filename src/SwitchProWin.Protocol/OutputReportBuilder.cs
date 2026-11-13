namespace SwitchProWin.Protocol;

/// <summary>
/// 输出报告构造器：子命令（0x01）、纯震动（0x10）、蓝牙握手（0x80 xx）。
/// 所有输出报告均为 64 字节（0x40）全长，hid_write 按全长发送。
/// </summary>
public static class OutputReportBuilder
{
    /// <summary>中性震动数据（关马达）：320Hz 0.0f + 160Hz 0.0f。</summary>
    public static readonly byte[] NeutralRumble =
    [
        0x00, 0x01, 0x40, 0x40, // 左
        0x00, 0x01, 0x40, 0x40, // 右
    ];

    /// <summary>包计数器（0x0..0xF 循环）。线程安全：UI/读线程/扫描线程会并发取号。</summary>
    public struct PacketCounter
    {
        private int _value;
        public byte Next() => (byte)(Interlocked.Increment(ref _value) & 0x0F);
        public void Reset() => Volatile.Write(ref _value, 0);
    }

    /// <summary>构造 0x01 输出报告：8 字节震动数据 + 子命令。</summary>
    public static byte[] BuildSubcommand(byte counter, byte subcommandId, ReadOnlySpan<byte> args = default)
    {
        var buf = new byte[ReportId.OutputReportLength];
        buf[0] = ReportId.RumbleAndSubcommand;
        buf[1] = counter;
        NeutralRumble.CopyTo(buf.AsSpan(2));
        buf[10] = subcommandId;
        args.CopyTo(buf.AsSpan(11));
        return buf;
    }

    /// <summary>构造 0x10 输出报告：纯震动。</summary>
    public static byte[] Rumble(byte counter, ReadOnlySpan<byte> rumbleData)
    {
        var buf = new byte[ReportId.OutputReportLength];
        buf[0] = ReportId.RumbleOnly;
        buf[1] = counter;
        rumbleData.CopyTo(buf.AsSpan(2));
        return buf;
    }

    // ---- 蓝牙握手 / USB 保活（M2 使用） ----

    /// <summary>0x80 0x04：USB 上强制 HID-only 模式，防止超时回蓝牙。</summary>
    public static byte[] UsbKeepAlive()
    {
        var buf = new byte[ReportId.OutputReportLength];
        buf[0] = ReportId.UartHandshake;
        buf[1] = 0x04;
        return buf;
    }

    // ---- 常用子命令封装 ----

    /// <summary>0x03：切换输入报告模式。</summary>
    public static byte[] SetInputReportMode(byte counter, InputMode mode)
        => BuildSubcommand(counter, Subcommand.SetInputReportMode, [(byte)mode]);

    /// <summary>0x40：开关 IMU。</summary>
    public static byte[] EnableImu(byte counter, bool enable)
        => BuildSubcommand(counter, Subcommand.EnableImu, [enable ? (byte)0x01 : (byte)0x00]);

    /// <summary>0x48：开关震动马达。</summary>
    public static byte[] EnableVibration(byte counter, bool enable)
        => BuildSubcommand(counter, Subcommand.EnableVibration, [enable ? (byte)0x01 : (byte)0x00]);

    /// <summary>0x30：设置玩家 LED。</summary>
    public static byte[] SetPlayerLeds(byte counter, PlayerLeds leds)
        => BuildSubcommand(counter, Subcommand.SetPlayerLeds, [(byte)leds]);

    /// <summary>0x10：读 SPI Flash。</summary>
    public static byte[] SpiRead(byte counter, uint address, byte length)
    {
        Span<byte> args = stackalloc byte[5];
        args[0] = (byte)address;
        args[1] = (byte)(address >> 8);
        args[2] = (byte)(address >> 16);
        args[3] = (byte)(address >> 24);
        args[4] = length;
        return BuildSubcommand(counter, Subcommand.SpiRead, args);
    }

    /// <summary>0x06：关机（闲置休眠用）。</summary>
    public static byte[] SetShutdown(byte counter)
        => BuildSubcommand(counter, Subcommand.SetShutdown);
}
