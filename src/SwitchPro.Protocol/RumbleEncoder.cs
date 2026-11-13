namespace SwitchPro.Protocol;

/// <summary>
/// Switch Pro 震动数据编码（dekuNukem rumble_data_table.md 算法）。
/// 每只线性谐振马达 4 字节：[HF频率低8位][HF幅度+HF频率高位][LF频率+LF幅度高位][LF幅度低8位]。
/// XInput 映射：LargeMotor(低频) → LF 带，SmallMotor(高频) → HF 带。
/// 幅度安全上限 1.0（表中 0xC8 处），超出会损伤马达。
/// </summary>
public static class RumbleEncoder
{
    /// <summary>持续震动的默认频率（Hz）：低频带 160 / 高频带 320，与 Switch 系统默认一致。</summary>
    public const double DefaultLfFreqHz = 160.0;
    public const double DefaultHfFreqHz = 320.0;

    /// <summary>XInput 0..255 → 幅度的比例上限（安全最大幅度）。</summary>
    public const float MaxSafeAmplitude = 1.0f;

    /// <summary>默认手感上限：XInput 满幅对应安全最大值的 50%（0.7 仍偏强，用户实测反馈）。</summary>
    public const float DefaultMaxAmplitude = 0.5f;

    /// <summary>编码 8 字节震动数据：左马达 4 字节 + 右马达 4 字节（两握把同幅驱动）。
    /// XInput：LargeMotor(低频) → LF 带幅度，SmallMotor(高频) → HF 带幅度。
    /// maxAmplitude：相对安全最大值(1.0)的比例，用于整体调弱手感（默认 0.5）。</summary>
    public static byte[] Encode(byte largeMotor, byte smallMotor, float maxAmplitude = DefaultMaxAmplitude)
    {
        if (maxAmplitude is < 0f or > MaxSafeAmplitude) maxAmplitude = DefaultMaxAmplitude;
        var left = Motor(
            hfFreqHz: DefaultHfFreqHz, hfAmp: smallMotor / 255f * maxAmplitude,
            lfFreqHz: DefaultLfFreqHz, lfAmp: largeMotor / 255f * maxAmplitude);
        var buf = new byte[8];
        left.CopyTo(buf, 0);
        left.CopyTo(buf, 4);
        return buf;
    }

    /// <summary>中性数据（关马达）：00 01 40 40 ×2。</summary>
    public static byte[] Neutral() => Encode(0, 0);

    /// <summary>编码单只马达的 4 字节（HF/LF 两带频率、幅度各自独立）。</summary>
    public static byte[] Motor(double hfFreqHz, float hfAmp, double lfFreqHz, float lfAmp)
    {
        // HF 频率：16 位码 (enc-0x60)*4，范围 0x004..0x1FC（81.75Hz 起才可表示）
        int hf16 = (FreqToCode(hfFreqHz) - 0x60) * 4;
        if (hf16 < 0x004) hf16 = 0x004;
        if (hf16 > 0x1FC) hf16 = 0x1FC;

        // LF 频率：8 位码 enc-0x40，范围 0x01..0x7F（40.9..626.3Hz）
        int lfCode = FreqToCode(lfFreqHz) - 0x40;
        if (lfCode < 0x01) lfCode = 0x01;
        if (lfCode > 0x7F) lfCode = 0x7F;

        byte hfAmpCode = AmpToCode(hfAmp);
        byte lfAmpCode = AmpToCode(lfAmp);
        int hfAmpByte = hfAmpCode * 2;                                          // 0x00..0xC8
        // LF 幅度 16 位：奇数编码 → 高位字节 0x80（中间档标志），低字节 = 编码/2+0x40
        int lfAmp16 = (lfAmpCode % 2 == 1 ? 0x8000 : 0x0000) | (lfAmpCode / 2 + 0x40); // 0x0040..0x8072

        return
        [
            (byte)(hf16 & 0xFF),
            (byte)(hfAmpByte + (hf16 >> 8)),          // HF 幅度 + HF 频率高位（0/1）
            (byte)(lfCode + (lfAmp16 >> 8)),          // LF 频率 + LF 幅度高位（0/0x80）
            (byte)(lfAmp16 & 0xFF),
        ];
    }

    /// <summary>频率 → 6 位编码：round(log2(f/10)×32)，范围 0x41..0xDF（41Hz..1252.6Hz）。</summary>
    public static byte FreqToCode(double freqHz)
    {
        if (freqHz < 41.0) freqHz = 41.0;
        if (freqHz > 1252.0) freqHz = 1252.0;
        int code = (int)Math.Round(Math.Log2(freqHz / 10.0) * 32.0);
        return (byte)Math.Clamp(code, 0x41, 0xDF);
    }

    /// <summary>幅度 → 7 位编码（三段对数，对应官方幅度表；0.0 → 0）。</summary>
    public static byte AmpToCode(float amp)
    {
        if (amp <= 0f) return 0;
        if (amp > MaxSafeAmplitude) amp = MaxSafeAmplitude;

        double code = amp > 0.235f
            ? Math.Log2(amp * 8.7f) * 32.0
            : amp > 0.1175f
                ? Math.Log2(amp * 17f) * 16.0
                : Math.Log2(amp * 120f) * 4.0;

        return (byte)Math.Clamp((int)Math.Round(code), 0, 100); // 100=0x64 → 安全上限
    }
}
