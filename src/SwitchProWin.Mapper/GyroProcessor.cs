using SwitchProWin.Protocol;

namespace SwitchProWin.Mapper;

/// <summary>体感（陀螺仪）工作模式。</summary>
public enum GyroMode
{
    /// <summary>关闭。</summary>
    Off,

    /// <summary>陀螺仪叠加到右摇杆（FPS 辅瞄，游戏内原生支持摇杆时使用）。</summary>
    RightStick,

    /// <summary>陀螺仪转鼠标移动（无视游戏支持，桌面级瞄准）。</summary>
    Mouse,
}

/// <summary>
/// 陀螺仪 → 输入 的转换器（DS4Windows/JoyShockMapper 同思路）。
/// 轴语义（Pro 手柄平放、摇杆朝上）：偏航 = GyrY、俯仰 = GyrX，可通过 SwapAxes/Invert 校正。
/// 每帧输入为一份 0x30 报告的 3 帧 IMU 样本（报告率 120Hz）。
/// 内置漂移补偿：启动采样校零 + 静止 3 秒自动重校零。
/// </summary>
public sealed class GyroProcessor
{
    private const double ReportPeriodSec = 1.0 / 120.0;
    private const double NoiseDeadzoneDps = 2.0;

    /// <summary>鼠标模式：度/秒 → 像素/秒 的基础比例。</summary>
    public const double MousePixelsPerDps = 2.0;

    /// <summary>右摇杆模式：累积 45 度/秒·秒 ≈ 满偏转。</summary>
    private const double StickFullDeflectionDps = 45.0;

    /// <summary>右摇杆模式每帧衰减（~120Hz 下约 12ms 半衰期量级，松手快速回中）。</summary>
    private const double StickDecay = 0.86;

    /// <summary>判定"静止"的角速度阈值（dps，扣除漂移后）。</summary>
    private const double StillThresholdDps = 1.2;

    public GyroMode Mode { get; set; } = GyroMode.Off;
    public double Sensitivity { get; set; } = 1.0;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public bool SwapAxes { get; set; }

    /// <summary>按住此键时体感才生效；null = 始终启用（由 Session 检查）。</summary>
    public ProButtons? ActivationButton { get; set; }

    /// <summary>静止自动重校零开关（默认开）。</summary>
    public bool AutoCalibrate { get; set; } = true;

    /// <summary>静止多久后重校零（毫秒）。公开以便测试。</summary>
    public long AutoCalibrateStillMs { get; set; } = 3000;

    // 漂移补偿
    private double _offsetYaw;
    private double _offsetPitch;
    private long _stillSinceMs = -1;

    private double _stickAccumX;
    private double _stickAccumY;

    /// <summary>右摇杆模式：根据 IMU 帧更新累积瞄准量，返回叠加到右摇杆的偏移（-1..1）。</summary>
    public (double X, double Y) UpdateRightStick(ReadOnlySpan<ImuSample> frames)
    {
        var (yaw, pitch) = CompensatedYawPitch(frames);

        if (Math.Abs(yaw) > NoiseDeadzoneDps)
            _stickAccumX += yaw * ReportPeriodSec / StickFullDeflectionDps * Sensitivity * Sign(InvertX);
        if (Math.Abs(pitch) > NoiseDeadzoneDps)
            _stickAccumY += pitch * ReportPeriodSec / StickFullDeflectionDps * Sensitivity * Sign(InvertY);

        _stickAccumX = Math.Clamp(_stickAccumX, -1, 1) * StickDecay;
        _stickAccumY = Math.Clamp(_stickAccumY, -1, 1) * StickDecay;
        if (Math.Abs(_stickAccumX) < 0.004) _stickAccumX = 0;
        if (Math.Abs(_stickAccumY) < 0.004) _stickAccumY = 0;

        return (_stickAccumX, _stickAccumY);
    }

    /// <summary>鼠标模式：返回本帧鼠标像素增量。</summary>
    public (double X, double Y) UpdateMouse(ReadOnlySpan<ImuSample> frames)
    {
        var (yaw, pitch) = CompensatedYawPitch(frames);

        double dx = Math.Abs(yaw) > NoiseDeadzoneDps
            ? yaw * ReportPeriodSec * MousePixelsPerDps * Sensitivity * Sign(InvertX)
            : 0;
        double dy = Math.Abs(pitch) > NoiseDeadzoneDps
            ? pitch * ReportPeriodSec * MousePixelsPerDps * Sensitivity * Sign(InvertY)
            : 0;

        return (ClampDelta(dx), ClampDelta(dy));
    }

    /// <summary>用一组静止样本立即校零（UI"重新校零"按钮 / 启动时调用）。</summary>
    public void CalibrateNow(ReadOnlySpan<ImuSample> frames)
    {
        var (yaw, pitch) = RawYawPitch(frames);
        _offsetYaw = yaw;
        _offsetPitch = pitch;
        _stillSinceMs = Environment.TickCount64;
    }

    /// <summary>模式切换/手柄断开/激活键松开时重置累积状态（不动漂移补偿）。</summary>
    public void Reset() => _stickAccumX = _stickAccumY = 0;

    /// <summary>完全重置（含漂移补偿），手柄重连时用。</summary>
    public void FullReset()
    {
        Reset();
        _offsetYaw = _offsetPitch = 0;
        _stillSinceMs = -1;
    }

    /// <summary>原始轴值（含漂移），并处理静止自动重校零。</summary>
    private (double yaw, double pitch) CompensatedYawPitch(ReadOnlySpan<ImuSample> frames)
    {
        var (yaw, pitch) = RawYawPitch(frames);
        double cy = yaw - _offsetYaw;
        double cp = pitch - _offsetPitch;

        if (AutoCalibrate)
        {
            long now = Environment.TickCount64;
            bool still = Math.Abs(cy) < StillThresholdDps && Math.Abs(cp) < StillThresholdDps;
            if (still)
            {
                if (_stillSinceMs < 0) _stillSinceMs = now;
                // 静止够久：把当前原始值吸收为新漂移基准
                if (now - _stillSinceMs >= AutoCalibrateStillMs)
                {
                    _offsetYaw = yaw;
                    _offsetPitch = pitch;
                    cy = cp = 0;
                }
            }
            else
            {
                _stillSinceMs = -1;
            }
        }

        return (cy, cp);
    }

    private (double yaw, double pitch) RawYawPitch(ReadOnlySpan<ImuSample> frames)
    {
        if (frames.IsEmpty)
            return (0, 0);

        double yaw = 0, pitch = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            yaw += frames[i].GyrY;
            pitch += frames[i].GyrX;
        }
        yaw /= frames.Length;
        pitch /= frames.Length;

        // SwapAxes 供实测轴位不符时纠正
        return SwapAxes ? (pitch, yaw) : (yaw, pitch);
    }

    private static int Sign(bool invert) => invert ? -1 : 1;

    private static double ClampDelta(double v) => Math.Clamp(v, -80, 80);
}
