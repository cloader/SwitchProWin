using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using SwitchProWin.Mapper;

namespace SwitchProWin.ViGEm;

/// <summary>
/// 虚拟 Xbox 360 手柄封装。依赖系统已安装 ViGEmBus 内核驱动。
/// Dispose 后虚拟手柄从系统移除（joy.cpl 里消失）。
/// </summary>
public sealed class VirtualXboxController : IDisposable
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _target;
    private readonly IVirtualGamepad _gamepad; // Connect/Disconnect/Submit 在此接口
    private Xbox360State _last;

    /// <summary>游戏下发震动（LargeMotor/SmallMotor 0..255）。</summary>
    public event Action<byte, byte>? RumbleReceived;

    /// <summary>虚拟手柄在系统中的厂商/产品 ID（XBOX 360 有线手柄）。</summary>
    public const ushort VendorId = 0x045E;
    public const ushort ProductId = 0x028E;

    /// <summary>系统分配的玩家编号（0..3）。</summary>
    public int UserIndex => _target.UserIndex;

    public VirtualXboxController()
    {
        _client = new ViGEmClient();
        _target = _client.CreateXbox360Controller();
        _gamepad = (IVirtualGamepad)_target;
        _target.FeedbackReceived += OnFeedbackReceived;
        _gamepad.Connect();
    }

    /// <summary>推送一帧状态（自动去重：状态未变化时不重复提交）。</summary>
    public void Update(Xbox360State state)
    {
        if (state.Equals(_last))
            return;
        _last = state;

        _target.SetButtonState(Xbox360Button.A, state.Buttons.HasFlag(XboxButtons.A));
        _target.SetButtonState(Xbox360Button.B, state.Buttons.HasFlag(XboxButtons.B));
        _target.SetButtonState(Xbox360Button.X, state.Buttons.HasFlag(XboxButtons.X));
        _target.SetButtonState(Xbox360Button.Y, state.Buttons.HasFlag(XboxButtons.Y));
        _target.SetButtonState(Xbox360Button.LeftShoulder, state.Buttons.HasFlag(XboxButtons.LeftShoulder));
        _target.SetButtonState(Xbox360Button.RightShoulder, state.Buttons.HasFlag(XboxButtons.RightShoulder));
        _target.SetButtonState(Xbox360Button.Back, state.Buttons.HasFlag(XboxButtons.Back));
        _target.SetButtonState(Xbox360Button.Start, state.Buttons.HasFlag(XboxButtons.Start));
        _target.SetButtonState(Xbox360Button.LeftThumb, state.Buttons.HasFlag(XboxButtons.LeftThumb));
        _target.SetButtonState(Xbox360Button.RightThumb, state.Buttons.HasFlag(XboxButtons.RightThumb));
        _target.SetButtonState(Xbox360Button.Guide, state.Buttons.HasFlag(XboxButtons.Guide));
        _target.SetButtonState(Xbox360Button.Up, state.Buttons.HasFlag(XboxButtons.DpadUp));
        _target.SetButtonState(Xbox360Button.Down, state.Buttons.HasFlag(XboxButtons.DpadDown));
        _target.SetButtonState(Xbox360Button.Left, state.Buttons.HasFlag(XboxButtons.DpadLeft));
        _target.SetButtonState(Xbox360Button.Right, state.Buttons.HasFlag(XboxButtons.DpadRight));

        _target.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxis(state.LX));
        _target.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxis(state.LY));
        _target.SetAxisValue(Xbox360Axis.RightThumbX, ToAxis(state.RX));
        _target.SetAxisValue(Xbox360Axis.RightThumbY, ToAxis(state.RY));
        _target.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
        _target.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);

        _gamepad.SubmitReport();
    }

    /// <summary>XInput 摇杆轴为 Int16 全量程（±32768），sbyte (-127..127) 需放大到满幅，
    /// 否则游戏里摇杆只有 ~0.4% 行程（表现为"没反应"）。</summary>
    private static short ToAxis(sbyte v)
        => (short)Math.Clamp(v * 258, (int)short.MinValue, (int)short.MaxValue);

    private void OnFeedbackReceived(object? sender, Xbox360FeedbackReceivedEventArgs e)
        => RumbleReceived?.Invoke(e.LargeMotor, e.SmallMotor);

    public void Dispose()
    {
        _target.FeedbackReceived -= OnFeedbackReceived;
        _gamepad.Disconnect();
        _client.Dispose();
    }
}
