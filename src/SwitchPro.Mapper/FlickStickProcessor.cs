namespace SwitchPro.Mapper;

/// <summary>
/// Flick Stick（甩杆瞄准，JoyShockMapper 同思路）：
/// 右摇杆改为"指向即转向"——推杆瞬间把镜头甩向该方向（角度 1:1），
/// 保持按住时以正比于偏转角的速率持续旋转；改变方向产生增量小甩。
/// 输出为每帧 yaw 角度增量（度），由会话累积到虚拟右摇杆。
/// </summary>
public sealed class FlickStickProcessor
{
    private const double EngageDeadzone = 0.30;      // 推过 30% 触发
    private const double FlickRateDegPerSec = 1200;  // 甩杆释放速率
    private const double RotateRateDegPerSec = 360;  // 满偏持续旋转速率（90° 偏转时）
    private const double FullDeflectionDeg = 45.0;   // 与陀螺仪瞄准一致的满偏当量

    /// <summary>灵敏度倍率（1.0 = 角度 1:1）。</summary>
    public double Sensitivity { get; set; } = 1.0;

    private bool _engaged;
    private double _lastAngle;
    private double _flickRemainingDeg;

    /// <summary>处理一帧。stick 为归一化右摇杆（x 右、y 上，-1..1）；
    /// periodSec 为调用周期。返回 yaw 角度增量（度，正=右转）。</summary>
    public double Update(double x, double y, double periodSec)
    {
        double mag = Math.Sqrt(x * x + y * y);
        if (mag < EngageDeadzone)
        {
            _engaged = false;
            _flickRemainingDeg = 0;
            return 0;
        }

        double angle = Math.Atan2(x, y); // 0=正上，顺时针为正（右=+90°）

        if (!_engaged)
        {
            _engaged = true;
            _flickRemainingDeg = angle * 180.0 / Math.PI * Sensitivity;
        }
        else
        {
            // 方向变化 → 追加增量甩（最短路径）
            double delta = angle - _lastAngle;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;
            _flickRemainingDeg += delta * 180.0 / Math.PI * Sensitivity;
        }
        _lastAngle = angle;

        // 甩杆相位：按固定速率释放剩余角度
        if (Math.Abs(_flickRemainingDeg) > 0.05)
        {
            double step = FlickRateDegPerSec * periodSec * Math.Sign(_flickRemainingDeg);
            if (Math.Abs(step) >= Math.Abs(_flickRemainingDeg))
            {
                double deg = _flickRemainingDeg;
                _flickRemainingDeg = 0;
                return deg;
            }
            _flickRemainingDeg -= step;
            return step;
        }

        // 旋转相位：速率 ∝ 偏转角与幅值
        return angle * 180.0 / Math.PI / 90.0 * RotateRateDegPerSec * mag * periodSec * Sensitivity;
    }

    /// <summary>满偏当量（度），会话把累积角度换算为摇杆偏移用。</summary>
    public static double FullDeflection => FullDeflectionDeg;

    public void Reset()
    {
        _engaged = false;
        _flickRemainingDeg = 0;
    }
}
