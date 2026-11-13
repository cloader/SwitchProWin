using SwitchPro.Protocol;
using Xunit;

namespace SwitchPro.Tests;

/// <summary>
/// 震动编码金标准测试。期望值全部对照 dekuNukem rumble_data_table.md 的表格：
/// 320Hz → HF 字节序 [00 01]；160Hz → LF 码 0x40；幅度 0.5 → HA 0x88 / LA [00 62]；
/// 幅度 1.0 → HA 0xC8 / LA [00 72]（安全上限）；中性包 = 00 01 40 40 ×2。
/// </summary>
public class RumbleEncoderTests
{
    [Fact]
    public void Zero_rumble_equals_neutral_packet()
    {
        var neutral = RumbleEncoder.Neutral();
        Assert.Equal(new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }, neutral);
        Assert.Equal(neutral, RumbleEncoder.Encode(0, 0));
    }

    [Theory]
    [InlineData(40.9, 0x41)]   // 表首 LF 01
    [InlineData(80.0, 0x60)]   // 2^(6/32)*10 = 79.99 → LF 0x20 边界
    [InlineData(160.0, 0x80)]  // 中性 LF 频率码
    [InlineData(320.0, 0xA0)]  // 中性 HF 频率码
    [InlineData(1252.0, 0xDF)] // 表尾
    public void Frequency_codes_match_table(double freq, byte expected)
    {
        Assert.Equal(expected, RumbleEncoder.FreqToCode(freq));
    }

    [Fact]
    public void Motor_320hz_half_amp_matches_table()
    {
        // HF 320Hz（码 0xA0 → hf16=0x100）、幅度 0.5（编码 0x44 → HA 0x88）
        // LF 160Hz（码 0x80 → LF 码 0x40）、幅度 0.5（LA 16位 = 0x0062）
        var m = RumbleEncoder.Motor(320f, 0.5f, 160f, 0.5f);
        Assert.Equal(new byte[] { 0x00, 0x89, 0x40, 0x62 }, m);
    }

    [Fact]
    public void Max_amplitude_is_safe()
    {
        // XInput 满幅（显式 100%）→ 幅度 1.0 → HA 0xC8（表中安全最大），LA [00 72]
        var m = RumbleEncoder.Motor(320f, 1.0f, 160f, 1.0f);
        Assert.Equal(new byte[] { 0x00, 0xC9, 0x40, 0x72 }, m);
    }

    [Fact]
    public void Default_scale_softens_full_deflection()
    {
        // 默认手感上限 0.5：幅度 0.5 → 编码 68 → HA 0x88(+1) / LA [00 62]
        var e = RumbleEncoder.Encode(255, 255);
        Assert.Equal(new byte[] { 0x00, 0x89, 0x40, 0x62, 0x00, 0x89, 0x40, 0x62 }, e);
    }

    [Fact]
    public void Out_of_range_scale_is_clamped_to_default()
    {
        Assert.Equal(RumbleEncoder.Encode(255, 255), RumbleEncoder.Encode(255, 255, 5.0f));
        Assert.Equal(RumbleEncoder.Encode(255, 255), RumbleEncoder.Encode(255, 255, -1f));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.012f, 2)]   // 表：0x04 → 0.0118（4*log2(a*120) 段）
    [InlineData(0.140f, 20)]  // 表：0x28 → 0.1397（16*log2(a*17) 段）
    [InlineData(0.325f, 48)]  // 表：0x60 → 0.3251（32*log2(a*8.7) 段）
    [InlineData(1.0f, 100)]   // 安全上限（表中 0xC8/2）
    [InlineData(5.0f, 100)]   // 超限钳制
    public void Amplitude_codes_match_table(float amp, byte expected)
    {
        Assert.Equal(expected, RumbleEncoder.AmpToCode(amp));
    }

    [Fact]
    public void Encode_applies_large_to_lf_and_small_to_hf()
    {
        // 仅大电机（默认 0.5 档，偶数编码无 0x80 标志）：LF 幅度非零（byte3 > 0x40），
        // HF 幅度为零（byte1 = 0x01）
        var m = RumbleEncoder.Encode(255, 0);
        Assert.Equal(0x01, m[1]);          // HF 无幅度 + 频率高位
        Assert.Equal(0x40, m[2]);          // LF 频率 160Hz
        Assert.True(m[3] > 0x40);          // LF 幅度 > 0

        // 仅小电机：HF 幅度非零（byte1 > 0x01），LF 幅度为零（byte2=0x40 byte3=0x40）
        var m2 = RumbleEncoder.Encode(0, 255);
        Assert.True(m2[1] > 0x01);         // HF 幅度 > 0
        Assert.Equal(0x40, m2[2]);         // LF 频率 160Hz
        Assert.Equal(0x40, m2[3]);         // LF 无幅度
    }

    [Fact]
    public void Left_and_right_motors_are_identical()
    {
        var e = RumbleEncoder.Encode(128, 64);
        Assert.Equal(e[0..4], e[4..8]);
    }

    [Fact]
    public void Rumble_packet_wraps_8_bytes()
    {
        var packet = OutputReportBuilder.Rumble(0x03, RumbleEncoder.Encode(255, 255, RumbleEncoder.MaxSafeAmplitude));
        Assert.Equal(0x10, packet[0]);
        Assert.Equal(0x03, packet[1]);
        Assert.Equal(0x00, packet[2]); // HF freq low
        Assert.Equal(0xC9, packet[3]); // HF amp max + 1
        Assert.Equal(0x72, packet[5]); // LF amp max
    }
}
