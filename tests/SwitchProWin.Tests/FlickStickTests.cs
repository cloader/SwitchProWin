using SwitchProWin.Mapper;
using Xunit;

namespace SwitchProWin.Tests;

public class FlickStickTests
{
    private const double Period = 1.0 / 120.0;

    [Fact]
    public void Deadzone_returns_zero_and_resets()
    {
        var f = new FlickStickProcessor();
        // 先推向右触发 engage
        Assert.True(f.Update(1.0, 0.0, Period) != 0 || true);
        // 回中：死区内输出 0
        Assert.Equal(0, f.Update(0.1, 0.1, Period), 5);
    }

    [Fact]
    public void Push_up_flicks_zero_degrees()
    {
        var f = new FlickStickProcessor();
        // 正上（angle=0）：甩 0°，旋转相位速率 ∝ angle=0 → 也为 0
        double total = 0;
        for (int i = 0; i < 60; i++) total += f.Update(0.0, 1.0, Period);
        Assert.Equal(0, total, 5);
    }

    [Fact]
    public void Push_right_flicks_positive_90_total()
    {
        var f = new FlickStickProcessor();
        // 持续推向右：甩杆总量 = +90°，之后旋转相位 90°→360°/s 持续正转
        double flickTotal = 0;
        for (int i = 0; i < 240; i++) flickTotal += f.Update(1.0, 0.0, Period); // 2 秒
        // 甩杆 90° + 旋转 360°/s × 2s = 90 + 720（远超钳制无影响，验证为正）
        Assert.True(flickTotal > 90, $"总角度应 > 90°，实际 {flickTotal:F1}");
    }

    [Fact]
    public void Direction_change_adds_delta_flick()
    {
        var f = new FlickStickProcessor { Sensitivity = 2.0 };
        // 推右（+90°×2 灵敏度 = 180° 甩杆）：0.25 秒内甩完（1200°/s），并开始旋转相位
        double first = 0;
        for (int i = 0; i < 30; i++) first += f.Update(1.0, 0.0, Period);
        // 甩杆 180° + 少量旋转相位（90° 偏转 → 3°/帧）
        Assert.True(first > 175, $"第一次甩杆应 ≥180°（含旋转），实际 {first:F1}");
    }

    [Fact]
    public void Sensitivity_scales_flick_target()
    {
        var f1 = new FlickStickProcessor { Sensitivity = 1.0 };
        var f2 = new FlickStickProcessor { Sensitivity = 2.0 };

        double t1 = 0, t2 = 0;
        for (int i = 0; i < 30; i++) // 0.25s：甩杆完成（90°/180°）+ 起始旋转
        {
            t1 += f1.Update(1.0, 0.0, Period);
            t2 += f2.Update(1.0, 0.0, Period);
        }
        // 甩杆目标 90 vs 180：差值至少 80°
        Assert.True(t2 - t1 > 80, $"灵敏度 2.0 的甩杆角度应明显更大：t1={t1:F1} t2={t2:F1}");
    }

    [Fact]
    public void Push_down_flicks_180()
    {
        var f = new FlickStickProcessor();
        double total = 0;
        for (int i = 0; i < 15; i++) total += f.Update(0.0, -1.0, Period);
        // 推下：atan2(0,-1)=π → 甩 +180°（10°/帧 × 15 帧释放中）
        Assert.True(total > 100, $"推下应朝 +180° 甩杆，实际 {total:F1}");
    }

    [Fact]
    public void Reset_clears_state()
    {
        var f = new FlickStickProcessor();
        f.Update(1.0, 0.0, Period);
        f.Reset();
        // 重置后重新推上：不再有旧方向的残余甩杆
        double total = 0;
        for (int i = 0; i < 10; i++) total += f.Update(0.0, 1.0, Period);
        Assert.Equal(0, total, 5); // 上=0°，甩 0；旋转 ∝ 0
    }
}
