using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SwitchProWin.Engine;
using SwitchProWin.Protocol;

namespace SwitchProWin.App;

/// <summary>
/// 摇杆软件校准向导：① 采集静止中心（2 秒）② 推满边缘画圈（5 秒）采集 min/max。
/// 结果写入当前配置文件的校准覆盖（不写手柄 SPI）。
/// </summary>
public partial class CalibrationWizard : Window
{
    private enum Phase { Idle, Center, Sweep, Done }

    private readonly ProControllerSession _session;
    private readonly bool _left;
    private Phase _phase = Phase.Idle;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    // 采样累积
    private int _samples;
    private double _sumX, _sumY;
    private int _minX = int.MaxValue, _maxX = int.MinValue, _minY = int.MaxValue, _maxY = int.MinValue;
    private DateTime _phaseStart;

    public CalibrationWizard(ProControllerSession session, bool left)
    {
        InitializeComponent();
        _session = session;
        _left = left;
        Title = Loc.F("cal.windowTitle", Loc.S(left ? "dir.left" : "dir.right"), session.PlayerIndex + 1);
        StepText.Text = Loc.F("cal.prepare", Loc.S(left ? "dir.left" : "dir.right"));
        _timer.Tick += (_, _) => Tick();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        _samples = 0; _sumX = _sumY = 0;
        _minX = int.MaxValue; _maxX = int.MinValue; _minY = int.MaxValue; _maxY = int.MinValue;
        _phase = Phase.Center;
        _phaseStart = DateTime.UtcNow;
        StepText.Text = Loc.S("cal.step1");
        HintText.Text = Loc.S("cal.centering");
        StartBtn.IsEnabled = false;
        _timer.Start();
    }

    private void Tick()
    {
        var state = _session.LatestState;
        if (state is null) return;
        var raw = _left ? state.Value.LeftStick : state.Value.RightStick;

        // 可视化（相对 SPI 校准归一化，仅示意位置）
        var (nx, ny) = _left
            ? _session.Profile.NormalizeLeftStick(raw)
            : _session.Profile.NormalizeRightStick(raw);
        Canvas.SetLeft(Dot, 100 + Math.Clamp(nx, -1, 1) * 71 - 9);
        Canvas.SetTop(Dot, 100 - Math.Clamp(ny, -1, 1) * 71 - 9);

        var elapsed = (DateTime.UtcNow - _phaseStart).TotalMilliseconds;

        switch (_phase)
        {
            case Phase.Center:
                _samples++; _sumX += raw.X; _sumY += raw.Y;
                Progress.Value = Math.Min(100, elapsed / 2000.0 * 100);
                if (elapsed >= 2000) EnterSweep();
                break;

            case Phase.Sweep:
                _samples++;
                _minX = Math.Min(_minX, raw.X); _maxX = Math.Max(_maxX, raw.X);
                _minY = Math.Min(_minY, raw.Y); _maxY = Math.Max(_maxY, raw.Y);
                Progress.Value = Math.Min(100, elapsed / 5000.0 * 100);
                if (elapsed >= 5000) Finish();
                break;
        }
    }

    private void EnterSweep()
    {
        // 记录中心（Center 阶段均值，含少量噪声钳制）
        _centerX = (int)Math.Clamp(_sumX / Math.Max(1, _samples), 0, 4095);
        _centerY = (int)Math.Clamp(_sumY / Math.Max(1, _samples), 0, 4095);
        _samples = 0;

        _phase = Phase.Sweep;
        _phaseStart = DateTime.UtcNow;
        StepText.Text = Loc.S("cal.step2");
        HintText.Text = Loc.S("cal.sweeping");
    }

    private void Finish()
    {
        _timer.Stop();
        _phase = Phase.Done;
        StartBtn.IsEnabled = true;

        if (_maxX - _minX < 800 || _maxY - _minY < 800)
        {
            StepText.Text = Loc.S("cal.tooSmall");
            HintText.Text = Loc.S("cal.tooSmallHint");
            return;
        }

        var cal = new StickCalibration
        {
            CenterX = (ushort)_centerX,
            CenterY = (ushort)_centerY,
            MinX = (ushort)_minX,
            MaxX = (ushort)_maxX,
            MinY = (ushort)_minY,
            MaxY = (ushort)_maxY,
        };

        // 写入配置覆盖并热应用
        var app = (App)Application.Current;
        if (_left) app.Engine.ActiveProfile.LeftStickCalibrationOverride = cal;
        else app.Engine.ActiveProfile.RightStickCalibrationOverride = cal;
        app.Engine.ApplyAndSave();

        StepText.Text = Loc.S("cal.done");
        HintText.Text = Loc.F("cal.result", _centerX, _centerY, _minX, _maxX, _minY, _maxY);
    }

    private int _centerX, _centerY;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        if (_left) app.Engine.ActiveProfile.LeftStickCalibrationOverride = null;
        else app.Engine.ActiveProfile.RightStickCalibrationOverride = null;
        // 恢复 SPI 原始校准
        if (_left) _session.Profile.LeftStickCalibration = _session.LeftStickCalibration;
        else _session.Profile.RightStickCalibration = _session.RightStickCalibration;
        app.Engine.ApplyAndSave();
        StepText.Text = Loc.S("cal.resetDone");
        HintText.Text = Loc.S("cal.resetHint");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
