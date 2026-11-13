using SwitchProWin.Protocol;
using Xunit;

namespace SwitchProWin.Tests;

/// <summary>
/// 0x30 报文金标准测试。样本按 dekuNukem 文档手工构造，
/// 摇杆解包期望值用文档中的 C 代码逐步验算。
/// </summary>
public class StandardFullReportTests
{
    /// <summary>构造 50 字节 0x30 报告。</summary>
    private static byte[] BuildReport(
        byte timer = 0x42,
        byte batteryConn = 0x8F, // 电量 8(满) 低 4 位连接信息 0xF? -> 用 0x8E
        byte rightButtons = 0x00,
        byte sharedButtons = 0x80, // magic 位
        byte leftButtons = 0x00,
        (ushort x, ushort y)? leftStick = null,
        (ushort x, ushort y)? rightStick = null)
    {
        var report = new byte[50];
        report[0] = 0x30;
        report[1] = timer;
        report[2] = batteryConn;
        report[3] = rightButtons;
        report[4] = sharedButtons;
        report[5] = leftButtons;

        void PackStick(int offset, (ushort x, ushort y)? stick)
        {
            var (x, y) = stick ?? ((ushort)0, (ushort)0);
            report[offset] = (byte)(x & 0xFF);
            report[offset + 1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
            report[offset + 2] = (byte)((y >> 4) & 0xFF);
        }

        PackStick(6, leftStick);
        PackStick(9, rightStick);
        return report;
    }

    [Fact]
    public void Parses_header_and_battery()
    {
        var report = BuildReport(timer: 0x77, batteryConn: 0x8E); // 电量 8=满, conn nibble E

        var ok = InputReportParser.TryParseStandardFullDefaultCal(report, out var state);

        Assert.True(ok);
        Assert.Equal(0x77, state.Timer);
        Assert.Equal(8, state.BatteryLevel);
    }

    [Fact]
    public void Charging_bit_is_excluded_from_battery_level()
    {
        // dekuNukem：电池 nibble 的 LSB 是充电标志，电量本身只有偶数值 0/2/4/6/8
        InputReportParser.TryParseStandardFullDefaultCal(
            BuildReport(batteryConn: 0x9E), out var charging); // 满电(8)+充电位
        Assert.Equal(8, charging.BatteryLevel);
        Assert.True(charging.Charging);

        InputReportParser.TryParseStandardFullDefaultCal(
            BuildReport(batteryConn: 0x4E), out var low); // 电量 4 未充电
        Assert.Equal(4, low.BatteryLevel);
        Assert.False(low.Charging);
    }

    [Fact]
    public void Parses_all_buttons()
    {
        // byte3: Y=bit0 X=bit1 B=bit2 A=bit3 SR=4 SL=5 R=6 ZR=7
        // byte4: Minus=0 Plus=1 RStick=2 LStick=3 Home=4 Capture=5 ChargingGrip=7
        // byte5: Down=0 Up=1 Right=2 Left=3 SR=4 SL=5 L=6 ZL=7
        var report = BuildReport(
            rightButtons: 0b1000_1111,          // ZR + A B X Y
            sharedButtons: 0b1011_1100,         // ChargingGrip Capture Home LStick RStick
            leftButtons: 0b1100_1111);          // ZL L SL SR + Left Right Up Down

        var ok = InputReportParser.TryParseStandardFullDefaultCal(report, out var state);

        Assert.True(ok);
        Assert.True(state.Buttons.HasFlag(ProButtons.A));
        Assert.True(state.Buttons.HasFlag(ProButtons.B));
        Assert.True(state.Buttons.HasFlag(ProButtons.X));
        Assert.True(state.Buttons.HasFlag(ProButtons.Y));
        Assert.True(state.Buttons.HasFlag(ProButtons.RStickPress));
        Assert.True(state.Buttons.HasFlag(ProButtons.LStickPress));
        Assert.True(state.Buttons.HasFlag(ProButtons.Home));
        Assert.True(state.Buttons.HasFlag(ProButtons.Capture));
        Assert.True(state.Buttons.HasFlag(ProButtons.ChargingGrip));
        Assert.True(state.Buttons.HasFlag(ProButtons.Up));
        Assert.True(state.Buttons.HasFlag(ProButtons.Down));
        Assert.True(state.Buttons.HasFlag(ProButtons.Left));
        Assert.True(state.Buttons.HasFlag(ProButtons.Right));
        Assert.True(state.Buttons.HasFlag(ProButtons.L));
        Assert.True(state.Buttons.HasFlag(ProButtons.Zl));
        Assert.False(state.Buttons.HasFlag(ProButtons.Minus));
        Assert.False(state.Buttons.HasFlag(ProButtons.Plus));
        Assert.False(state.Buttons.HasFlag(ProButtons.R));
        Assert.True(state.Buttons.HasFlag(ProButtons.Zr)); // rightButtons bit7
    }

    [Fact]
    public void Unpacks_sticks_per_documented_algorithm()
    {
        // 文档算法：x = d[0] | ((d[1]&0xF)<<8); y = (d[1]>>4) | (d[2]<<4)
        // 构造: x=0x123, y=0x456 -> d = [0x23, 0x61, 0x45]
        var report = BuildReport(
            leftStick: (0x123, 0x456),
            rightStick: (0x0ABC, 0x0DEF));

        var ok = InputReportParser.TryParseStandardFullDefaultCal(report, out var state);

        Assert.True(ok);
        Assert.Equal(0x123, state.LeftStick.X);
        Assert.Equal(0x456, state.LeftStick.Y);
        Assert.Equal(0x0ABC, state.RightStick.X);
        Assert.Equal(0x0DEF, state.RightStick.Y);
    }

    [Fact]
    public void Rejects_wrong_report_id_or_short_buffer()
    {
        var report = BuildReport();
        report[0] = 0x21;
        Assert.False(InputReportParser.TryParseStandardFullDefaultCal(report, out _));

        var shortReport = new byte[10];
        shortReport[0] = 0x30;
        Assert.False(InputReportParser.TryParseStandardFullDefaultCal(shortReport, out _));
    }

    [Fact]
    public void Parses_three_imu_frames()
    {
        var report = BuildReport();
        // IMU 数据在字节 13-48：每帧 12 字节（accX accY accZ gyrX gyrY gyrZ Int16LE）
        BitConverter.GetBytes((short)1000).CopyTo(report, 13);           // 第 1 帧 accX
        BitConverter.GetBytes((short)-2000).CopyTo(report, 13 + 24 + 6); // 第 3 帧 gyrX

        var ok = InputReportParser.TryParseStandardFullDefaultCal(report, out var state);

        Assert.True(ok);
        Assert.Equal(3, state.Imu.Length);
        // 灵敏度：acc 4096 LSB/g、gyro 6.6855 LSB/dps
        Assert.Equal(1000f / 4096f, state.Imu.Span[0].AccX, 5);
        Assert.Equal(-2000f / 6.6855f, state.Imu.Span[2].GyrX, 2);
    }
}
