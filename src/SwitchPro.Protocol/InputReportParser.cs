using System.Buffers.Binary;

namespace SwitchPro.Protocol;

/// <summary>
/// 输入报告解析器。覆盖 USB/蓝牙共用的三种报告：
/// 0x30 标准全量、0x21 子命令应答、0x3F 简单 HID。
/// 按钮位表与摇杆解包算法来自 dekuNukem《Nintendo Switch Reverse Engineering》。
/// </summary>
public static class InputReportParser
{
    /// <summary>解析 0x30 标准全量报告（50 字节，含 3 帧 IMU），使用默认校准。</summary>
    public static bool TryParseStandardFullDefaultCal(ReadOnlySpan<byte> report, out ProControllerState state)
        => TryParseStandardFull(report, StickCalibration.Default, StickCalibration.Default,
            ImuCalibration.Default, out state);

    /// <summary>解析 0x30 标准全量报告，使用给定校准数据。</summary>
    public static bool TryParseStandardFull(
        ReadOnlySpan<byte> report,
        StickCalibration left, StickCalibration right,
        ImuCalibration imu,
        out ProControllerState state)
        => TryParseStandardFull(report, left, right, imu, imuBuffer: null, out state);

    /// <summary>解析 0x30 标准全量报告（零分配重载：IMU 帧写入调用方缓冲）。</summary>
    public static bool TryParseStandardFull(
        ReadOnlySpan<byte> report,
        StickCalibration left, StickCalibration right,
        ImuCalibration imu,
        ImuSample[]? imuBuffer,
        out ProControllerState state)
    {
        state = default;
        if (report.Length < ReportId.StandardFullInputLength || report[0] != ReportId.StandardFull)
            return false;

        byte timer = report[1];
        byte batteryConn = report[2];
        var buttons = (ProButtons)
            ((uint)report[3] | ((uint)report[4] << 8) | ((uint)report[5] << 16));

        var leftStick = UnpackStick(report.Slice(6, 3));
        var rightStick = UnpackStick(report.Slice(9, 3));

        var samples = imuBuffer ?? new ImuSample[3];
        for (int i = 0; i < 3; i++)
        {
            // 实测（抓包验证）：IMU 数据在字节 13-48，3 帧 × 12 字节；
            // 字节 12 为状态字节（恒 0）。偏移 12 会读到错位垃圾。
            int off = 13 + i * 12;
            samples[i] = UnpackImu(report.Slice(off, 12), imu);
        }

        state = new ProControllerState
        {
            Timer = timer,
            // 电池 nibble 的 LSB 是充电位（dekuNukem: "Battery level... LSB=Charging"），
            // 电量本身只有偶数值 0/2/4/6/8，读数需剥掉充电位
            BatteryLevel = (batteryConn >> 4) & 0xE,
            Charging = (batteryConn & 0x10) != 0,
            IsJoyConType = ((batteryConn >> 1) & 3) != 0, // 3=Joy-Con, 0=Pro/充电握把
            Buttons = buttons,
            LeftStick = leftStick,
            RightStick = rightStick,
            Imu = samples,
        };
        return true;
    }

    /// <summary>解析 0x21 子命令应答报告（前 12 字节与 0x30 同构）。</summary>
    public static bool TryParseSubcommandReply(
        ReadOnlySpan<byte> report,
        out byte ackFlags, out byte subcommandId, out ReadOnlySpan<byte> payload,
        out ProControllerState state)
    {
        state = default;
        ackFlags = 0;
        subcommandId = 0;
        payload = default;

        if (report.Length < 16 || report[0] != ReportId.SubcommandReply)
            return false;

        byte batteryConn = report[2];
        var buttons = (ProButtons)
            ((uint)report[3] | ((uint)report[4] << 8) | ((uint)report[5] << 16));

        state = new ProControllerState
        {
            Timer = report[1],
            BatteryLevel = (batteryConn >> 4) & 0xE, // 剥掉 LSB 充电位
            Charging = (batteryConn & 0x10) != 0,
            IsJoyConType = ((batteryConn >> 1) & 3) != 0,
            Buttons = buttons,
            LeftStick = UnpackStick(report.Slice(6, 3)),
            RightStick = UnpackStick(report.Slice(9, 3)),
        };

        ackFlags = report[13];
        subcommandId = report[14];
        payload = report.Slice(15);
        return true;
    }

    /// <summary>解析 0x3F 简单 HID 报告（Pro 手柄 12 字节）。</summary>
    public static bool TryParseSimple(ReadOnlySpan<byte> report, out ProControllerState state)
    {
        state = default;
        if (report.Length < ReportId.SimpleInputLength || report[0] != ReportId.SimpleHid)
            return false;

        // 简单模式位表：byte1 低 4 位方向键、bit4/5 SL/SR、byte2 按键与肩键
        // 与 0x30 的位布局不同，这里显式映射。
        ProButtons b = ProButtons.None;
        if ((report[1] & 0x01) != 0) b |= ProButtons.Down;
        if ((report[1] & 0x02) != 0) b |= ProButtons.Right;
        if ((report[1] & 0x04) != 0) b |= ProButtons.Left;
        if ((report[1] & 0x08) != 0) b |= ProButtons.Up;
        if ((report[1] & 0x10) != 0) b |= ProButtons.SlLeft;
        if ((report[1] & 0x20) != 0) b |= ProButtons.SrLeft;
        if ((report[2] & 0x01) != 0) b |= ProButtons.Minus;
        if ((report[2] & 0x02) != 0) b |= ProButtons.Plus;
        if ((report[2] & 0x04) != 0) b |= ProButtons.LStickPress;
        if ((report[2] & 0x08) != 0) b |= ProButtons.RStickPress;
        if ((report[2] & 0x10) != 0) b |= ProButtons.Home;
        if ((report[2] & 0x20) != 0) b |= ProButtons.Capture;
        if ((report[2] & 0x40) != 0) b |= ProButtons.L;   // L / R 共用位
        if ((report[2] & 0x80) != 0) b |= ProButtons.Zl;  // ZL / ZR 共用位

        state = new ProControllerState
        {
            Buttons = b,
            // 每摇杆 4 字节：16 位水平 + 16 位垂直（dekuNukem simple HID：
            // horizontal = data[0] | data[1]<<8，vertical = data[2] | data[3]<<8）
            LeftStick = new RawStick(
                (ushort)(report[4] | (report[5] << 8)),
                (ushort)(report[6] | (report[7] << 8))),
            RightStick = new RawStick(
                (ushort)(report[8] | (report[9] << 8)),
                (ushort)(report[10] | (report[11] << 8))),
        };
        return true;
    }

    /// <summary>3 字节解包为两个 12 位值：水平在前、垂直在后。</summary>
    public static RawStick UnpackStick(ReadOnlySpan<byte> data)
        => new(
            X: (ushort)(data[0] | ((data[1] & 0x0F) << 8)),
            Y: (ushort)((data[1] >> 4) | (data[2] << 4)));

    /// <summary>12 字节解包为一帧 IMU（Acc XYZ + Gyro XYZ，均为 Int16LE）。</summary>
    public static ImuSample UnpackImu(ReadOnlySpan<byte> data, ImuCalibration cal)
    {
        short accX = BinaryPrimitives.ReadInt16LittleEndian(data);
        short accY = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2));
        short accZ = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(4));
        short gyrX = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(6));
        short gyrY = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(8));
        short gyrZ = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(10));

        return new ImuSample(
            AccX: accX / cal.AccSensitivityPerG,
            AccY: accY / cal.AccSensitivityPerG,
            AccZ: accZ / cal.AccSensitivityPerG,
            GyrX: gyrX / cal.GyrSensitivityPerDps,
            GyrY: gyrY / cal.GyrSensitivityPerDps,
            GyrZ: gyrZ / cal.GyrSensitivityPerDps);
    }
}
