using SwitchProWin.Mapper;
using SwitchProWin.Protocol;
using Xunit;

namespace SwitchProWin.Tests;

public class GyroProcessorTests
{
    /// <summary>构造一份 0x30 报告的 3 帧 IMU 样本（同一角速度重复 3 次）。</summary>
    private static ImuSample[] Frames(float gyrX, float gyrY, float gyrZ)
        => Enumerable.Range(0, 3).Select(_ => new ImuSample(
            AccX: 0, AccY: 0, AccZ: 1f,
            GyrX: gyrX, GyrY: gyrY, GyrZ: gyrZ)).ToArray();

    private static GyroProcessor New(GyroMode mode, double sens = 1.0,
        bool invX = false, bool invY = false, bool swap = false)
        => new() { Mode = mode, Sensitivity = sens, InvertX = invX, InvertY = invY, SwapAxes = swap };

    [Fact]
    public void Mouse_mode_converts_yaw_to_horizontal_pixels()
    {
        var gyro = New(GyroMode.Mouse);
        // yaw 默认取 GyrY：100 dps × (1/120)s × 2 px/dps = 1.667 px
        var (dx, dy) = gyro.UpdateMouse(Frames(0, 100, 0));
        Assert.Equal(100 / 120.0 * 2.0, dx, 3);
        Assert.Equal(0, dy, 5);
    }

    [Fact]
    public void Mouse_mode_pitch_drives_vertical()
    {
        var gyro = New(GyroMode.Mouse);
        var (_, dy) = gyro.UpdateMouse(Frames(60, 0, 0));
        Assert.Equal(60 / 120.0 * 2.0, dy, 3);
    }

    [Fact]
    public void Mouse_mode_deadzone_suppresses_noise()
    {
        var gyro = New(GyroMode.Mouse);
        var (dx, dy) = gyro.UpdateMouse(Frames(1.5f, 1.0f, 0)); // < 2 dps 静噪
        Assert.Equal(0, dx, 5);
        Assert.Equal(0, dy, 5);
    }

    [Fact]
    public void Mouse_mode_invert_flips_sign()
    {
        var gyro = New(GyroMode.Mouse, invX: true);
        var (dx, _) = gyro.UpdateMouse(Frames(0, 100, 0));
        Assert.True(dx < 0);

        var gyro2 = New(GyroMode.Mouse, invY: true);
        var (_, dy) = gyro2.UpdateMouse(Frames(60, 0, 0));
        Assert.True(dy < 0);
    }

    [Fact]
    public void Mouse_mode_swap_axes_exchanges_yaw_and_pitch()
    {
        var gyro = New(GyroMode.Mouse, swap: true);
        // 交换后：GyrX 驱动水平，GyrY 驱动垂直
        var (dx, dy) = gyro.UpdateMouse(Frames(60, 0, 0));
        Assert.Equal(60 / 120.0 * 2.0, dx, 3);
        Assert.Equal(0, dy, 5);
    }

    [Fact]
    public void Mouse_mode_sensitivity_scales_output()
    {
        var gyro = New(GyroMode.Mouse, sens: 2.0);
        var (dx, _) = gyro.UpdateMouse(Frames(0, 100, 0));
        Assert.Equal(100 / 120.0 * 2.0 * 2.0, dx, 3);
    }

    [Fact]
    public void Mouse_mode_delta_is_bounded()
    {
        var gyro = New(GyroMode.Mouse);
        // 陀螺仪满量程 ±2450dps（±32767/13371）：2000dps → 33.3px/帧，仍在钳制上限内
        var (dx, _) = gyro.UpdateMouse(Frames(0, 2000, 0));
        Assert.Equal(2000 / 120.0 * 2.0, dx, 3);
        Assert.True(Math.Abs(dx) < 80);
    }

    [Fact]
    public void Right_stick_mode_accumulates_and_decays()
    {
        var gyro = New(GyroMode.RightStick, sens: 10.0);
        double lastDx = 0;
        // 持续 60dps 偏航：累积量应先增长后趋于饱和/衰减平衡
        for (int i = 0; i < 200; i++)
        {
            var (dx, _) = gyro.UpdateRightStick(Frames(0, 60, 0));
            Assert.True(dx >= lastDx - 0.5);
            lastDx = dx;
            Assert.True(Math.Abs(dx) <= 1.0);
        }
        // 停止转动后应衰减回 0
        for (int i = 0; i < 300; i++)
            gyro.UpdateRightStick(Frames(0, 0, 0));
        var (endDx, _) = gyro.UpdateRightStick(Frames(0, 0, 0));
        Assert.Equal(0, endDx, 3);
    }

    [Fact]
    public void Right_stick_mode_direction()
    {
        var gyro = New(GyroMode.RightStick);
        var (dx, _) = gyro.UpdateRightStick(Frames(0, 60, 0));
        Assert.True(dx > 0); // 正偏航 → 右转

        var gyroInv = New(GyroMode.RightStick, invX: true);
        var (dxInv, _) = gyroInv.UpdateRightStick(Frames(0, 60, 0));
        Assert.True(dxInv < 0);
    }

    [Fact]
    public void Empty_frames_return_zero()
    {
        var gyro = New(GyroMode.Mouse);
        var (dx, dy) = gyro.UpdateMouse(ReadOnlySpan<ImuSample>.Empty);
        Assert.Equal(0, dx, 5);
        Assert.Equal(0, dy, 5);

        var (sx, sy) = gyro.UpdateRightStick(ReadOnlySpan<ImuSample>.Empty);
        Assert.Equal(0, sx, 5);
        Assert.Equal(0, sy, 5);
    }

    [Fact]
    public void Reset_clears_stick_accumulation()
    {
        var gyro = New(GyroMode.RightStick, sens: 10.0);
        gyro.UpdateRightStick(Frames(0, 60, 0));
        gyro.Reset();
        var (dx, _) = gyro.UpdateRightStick(Frames(0f, 0f, 0f));
        Assert.Equal(0, dx, 5);
    }

    // ================= 漂移补偿 / 自动校零 =================

    [Fact]
    public void CalibrateNow_absorbs_constant_drift()
    {
        var gyro = New(GyroMode.Mouse);
        gyro.AutoCalibrate = false; // 关自动，验证手动校零
        // 模拟漂移：静止时陀螺仪恒读 +5 dps
        gyro.CalibrateNow(Frames(5f, 5f, 0f));
        var (dx, dy) = gyro.UpdateMouse(Frames(5f, 5f, 0f));
        Assert.Equal(0, dx, 5);
        Assert.Equal(0, dy, 5);

        // 真实运动仍然有输出：10 dps 实际 = 10-5 = 5 dps
        var (dx2, _) = gyro.UpdateMouse(Frames(5f, 10f, 0f));
        Assert.Equal(5 / 120.0 * 2.0, dx2, 3);
    }

    [Fact]
    public void AutoCalibrate_absorbs_drift_after_still_period()
    {
        var gyro = New(GyroMode.Mouse);
        gyro.AutoCalibrateStillMs = 0; // 立即吸收
        gyro.CalibrateNow(Frames(0f, 0f, 0f)); // 基准 0

        // 1.0 dps 小漂移（低于 1.2 静止阈值）：喂两轮后被吸收为基准
        gyro.UpdateMouse(Frames(0f, 1.0f, 0f));
        gyro.UpdateMouse(Frames(0f, 1.0f, 0f));

        // 真实运动 5 dps：补偿后 = 5-1 = 4（若未吸收会是 5）
        var (dx, _) = gyro.UpdateMouse(Frames(0, 5, 0));
        Assert.Equal(4 / 120.0 * 2.0, dx, 3);
    }

    [Fact]
    public void AutoCalibrate_does_not_reset_while_moving()
    {
        var gyro = New(GyroMode.Mouse);
        gyro.AutoCalibrateStillMs = 0;
        gyro.CalibrateNow(Frames(0f, 0f, 0f));
        // 大角速度持续输入：输出不应被吸收为漂移
        var (dx1, _) = gyro.UpdateMouse(Frames(0, 100, 0));
        var (dx2, _) = gyro.UpdateMouse(Frames(0, 100, 0));
        Assert.Equal(dx1, dx2, 5);
        Assert.True(dx2 > 0);
    }

    [Fact]
    public void FullReset_clears_offsets()
    {
        var gyro = New(GyroMode.Mouse);
        gyro.AutoCalibrate = false;
        gyro.CalibrateNow(Frames(5f, 5f, 0f));
        gyro.FullReset();
        // 补偿被清除：5 dps 漂移重新可见（但低于 2dps 静噪？5 > 2 有输出）
        var (dx, _) = gyro.UpdateMouse(Frames(0f, 5f, 0f));
        Assert.Equal(5 / 120.0 * 2.0, dx, 3);
    }

    [Fact]
    public void Activation_button_is_configurable()
    {
        var gyro = New(GyroMode.RightStick);
        Assert.Null(gyro.ActivationButton); // 默认始终启用
        gyro.ActivationButton = ProButtons.Zl;
        Assert.Equal(ProButtons.Zl, gyro.ActivationButton);
    }
}
