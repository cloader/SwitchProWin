namespace SwitchPro.Protocol;

/// <summary>
/// 摇杆校准数据（出厂或用户）。
/// 9 字节 = 6 个 12 位值，出厂数据含义为 [max_x, max_y, center_x, center_y, min_x, min_y] 的
/// 偏移量形式；此处统一换算为绝对范围。
/// </summary>
public readonly record struct StickCalibration
{
    public ushort CenterX { get; init; }
    public ushort CenterY { get; init; }
    public ushort MinX { get; init; }
    public ushort MaxX { get; init; }
    public ushort MinY { get; init; }
    public ushort MaxY { get; init; }

    /// <summary>USB 兜底值：读不到 SPI 校准时的近似中心/量程。</summary>
    public static StickCalibration Default => new()
    {
        CenterX = 2048,
        CenterY = 2048,
        MinX = 2048 - 1500,
        MaxX = 2048 + 1500,
        MinY = 2048 - 1500,
        MaxY = 2048 + 1500,
    };

    /// <summary>解码 9 字节出厂/用户校准数据。</summary>
    /// <param name="data">9 字节原始数据（用户校准需先剥掉 2 字节 magic）。</param>
    public static StickCalibration Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9)
            throw new ArgumentException("stick calibration requires 9 bytes", nameof(data));

        // 12 位解包（dekuNukem spi_flash_notes.md）：
        // d0 = (b1 << 8) & 0xF00 | b0;  d1 = (b2 << 4) | (b1 >> 4);  以此类推
        ushort d0 = (ushort)((((uint)data[1] << 8) & 0xF00) | data[0]);
        ushort d1 = (ushort)((((uint)data[2] & 0xFF) << 4) | ((uint)data[1] >> 4));
        ushort d2 = (ushort)((((uint)data[4] << 8) & 0xF00) | data[3]);
        ushort d3 = (ushort)((((uint)data[5] & 0xFF) << 4) | ((uint)data[4] >> 4));
        ushort d4 = (ushort)((((uint)data[7] << 8) & 0xF00) | data[6]);
        ushort d5 = (ushort)((((uint)data[8] & 0xFF) << 4) | ((uint)data[7] >> 4));

        // 左摇杆顺序: [max_x, max_y, center_x, center_y, min_x, min_y]
        ushort centerX = d2, centerY = d3;
        return new StickCalibration
        {
            CenterX = centerX,
            CenterY = centerY,
            MaxX = (ushort)(centerX + d0),
            MaxY = (ushort)(centerY + d1),
            MinX = (ushort)(centerX - d4),
            MinY = (ushort)(centerY - d5),
        };
    }

    /// <summary>右摇杆出厂数据的顺序与左摇杆不同：[center_x, center_y, min_x, min_y, max_x, max_y]。</summary>
    public static StickCalibration DecodeRightFactory(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9)
            throw new ArgumentException("stick calibration requires 9 bytes", nameof(data));

        ushort d0 = (ushort)((((uint)data[1] << 8) & 0xF00) | data[0]);
        ushort d1 = (ushort)((((uint)data[2] & 0xFF) << 4) | ((uint)data[1] >> 4));
        ushort d2 = (ushort)((((uint)data[4] << 8) & 0xF00) | data[3]);
        ushort d3 = (ushort)((((uint)data[5] & 0xFF) << 4) | ((uint)data[4] >> 4));
        ushort d4 = (ushort)((((uint)data[7] << 8) & 0xF00) | data[6]);
        ushort d5 = (ushort)((((uint)data[8] & 0xFF) << 4) | ((uint)data[7] >> 4));

        ushort centerX = d0, centerY = d1;
        return new StickCalibration
        {
            CenterX = centerX,
            CenterY = centerY,
            MinX = (ushort)(centerX - d2),
            MinY = (ushort)(centerY - d3),
            MaxX = (ushort)(centerX + d4),
            MaxY = (ushort)(centerY + d5),
        };
    }
}

/// <summary>IMU 校准（默认灵敏度：加速度 ±8G、陀螺仪 ±2000dps）。</summary>
public readonly record struct ImuCalibration(float AccSensitivityPerG, float GyrSensitivityPerDps)
{
    /// <summary>
    /// int16 满幅 ÷ 量程：加速度 32767/8 = 4096 LSB/g；
    /// 陀螺仪 13371 LSB/2000dps = 6.6855 LSB/dps（实测静止噪声 ±2dps 验证）。
    /// </summary>
    public static ImuCalibration Default => new(4096f, 6.6855f);
}
