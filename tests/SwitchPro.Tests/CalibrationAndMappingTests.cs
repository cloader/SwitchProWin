using SwitchPro.Engine;
using SwitchPro.Mapper;
using SwitchPro.Protocol;
using Xunit;

namespace SwitchPro.Tests;

public class CalibrationTests
{
    /// <summary>dekuNukem spi_flash_notes.md 的左摇杆出厂数据样例：
    /// d = [0x4F7 max_x, 0x424 max_y, 0x79F center_x, 0x8A0 center_y, 0x510 min_x, 0x479 min_y]。</summary>
    [Fact]
    public void Decodes_left_factory_calibration()
    {
        // 编码 6 个 12 位值 -> 9 字节（编码算法与解码对称）
        byte[] encoded = Encode12Bit(new ushort[] { 0x4F7, 0x424, 0x79F, 0x8A0, 0x510, 0x479 });

        var cal = StickCalibration.Decode(encoded);

        Assert.Equal(0x79F, cal.CenterX);
        Assert.Equal(0x8A0, cal.CenterY);
        Assert.Equal(0x79F + 0x4F7, cal.MaxX);
        Assert.Equal(0x8A0 + 0x424, cal.MaxY);
        Assert.Equal(0x79F - 0x510, cal.MinX);
        Assert.Equal(0x8A0 - 0x479, cal.MinY);
    }

    /// <summary>右摇杆出厂数据顺序：[center_x, center_y, min_x, min_y, max_x, max_y]。</summary>
    [Fact]
    public void Decodes_right_factory_calibration()
    {
        byte[] encoded = Encode12Bit(new ushort[] { 0x79F, 0x8A0, 0x510, 0x479, 0x4F7, 0x424 });

        var cal = StickCalibration.DecodeRightFactory(encoded);

        Assert.Equal(0x79F, cal.CenterX);
        Assert.Equal(0x8A0, cal.CenterY);
        Assert.Equal(0x79F + 0x4F7, cal.MaxX);
        Assert.Equal(0x8A0 + 0x424, cal.MaxY);
        Assert.Equal(0x79F - 0x510, cal.MinX);
        Assert.Equal(0x8A0 - 0x479, cal.MinY);
    }

    /// <summary>12 位值打包为 9 字节（与手柄 SPI 中的布局一致，供测试构造）。</summary>
    private static byte[] Encode12Bit(ushort[] values)
    {
        var data = new byte[9];
        for (int i = 0; i < 3; i++)
        {
            uint v0 = values[i * 2];
            uint v1 = values[i * 2 + 1];
            data[i * 3 + 0] = (byte)(v0 & 0xFF);
            data[i * 3 + 1] = (byte)(((v0 >> 8) & 0x0F) | ((v1 & 0x0F) << 4));
            data[i * 3 + 2] = (byte)((v1 >> 4) & 0xFF);
        }
        return data;
    }
}

public class MappingProfileTests
{
    private static ProControllerState StateWith(ProButtons buttons, RawStick? left = null, RawStick? right = null)
        => new()
        {
            Buttons = buttons,
            LeftStick = left ?? new RawStick(2048, 2048),
            RightStick = right ?? new RawStick(2048, 2048),
        };

    [Fact]
    public void Nintendo_layout_swaps_A_B_and_X_Y()
    {
        var profile = new MappingProfile { NintendoLayout = true };

        var mapped = profile.Map(StateWith(ProButtons.A | ProButtons.Y));
        Assert.True(mapped.Buttons.HasFlag(XboxButtons.B));
        Assert.True(mapped.Buttons.HasFlag(XboxButtons.X));
        Assert.False(mapped.Buttons.HasFlag(XboxButtons.A));

        var mapped2 = profile.Map(StateWith(ProButtons.B | ProButtons.X));
        Assert.True(mapped2.Buttons.HasFlag(XboxButtons.A));
        Assert.True(mapped2.Buttons.HasFlag(XboxButtons.Y));
    }

    [Fact]
    public void Xbox_layout_keeps_A_B()
    {
        var profile = new MappingProfile { NintendoLayout = false };
        var mapped = profile.Map(StateWith(ProButtons.A | ProButtons.B));
        Assert.True(mapped.Buttons.HasFlag(XboxButtons.A));
        Assert.True(mapped.Buttons.HasFlag(XboxButtons.B));
    }

    [Fact]
    public void Zl_Zr_map_to_full_triggers()
    {
        var profile = new MappingProfile();
        var mapped = profile.Map(StateWith(ProButtons.Zl | ProButtons.Zr));
        Assert.Equal(255, mapped.LeftTrigger);
        Assert.Equal(255, mapped.RightTrigger);
    }

    [Fact]
    public void Centered_stick_outputs_zero()
    {
        var profile = new MappingProfile();
        var mapped = profile.Map(StateWith(ProButtons.None));
        Assert.Equal(0, mapped.LX);
        Assert.Equal(0, mapped.LY);
        Assert.Equal(0, mapped.RX);
        Assert.Equal(0, mapped.RY);
    }

    [Fact]
    public void Stick_up_gives_positive_LY()
    {
        var profile = new MappingProfile();
        // 左摇杆推满上（Y 增大方向）→ XInput 语义：正 LY = 上（ViGEm 实测验证）
        var mapped = profile.Map(StateWith(ProButtons.None, left: new RawStick(2048, 3548)));
        Assert.Equal(0, mapped.LX);
        Assert.True(mapped.LY > 0, $"期望上推为正 LY，实际 {mapped.LY}");
        Assert.Equal(127, mapped.LY);
    }

    [Fact]
    public void Deadzone_suppresses_small_movements()
    {
        var profile = new MappingProfile();
        profile.LeftStick.InnerDeadzone = 0.20;

        var mapped = profile.Map(StateWith(ProButtons.None, left: new RawStick(2048 + 150, 2048)));
        Assert.Equal(0, mapped.LX);
    }

    [Fact]
    public void Home_maps_to_guide()
    {
        var profile = new MappingProfile();
        var mapped = profile.Map(StateWith(ProButtons.Home));
        Assert.True(mapped.Buttons.HasFlag(XboxButtons.Guide));
    }
}

public class StickSettingsTests
{
    [Fact]
    public void Zero_input_passes_through()
    {
        var s = new StickSettings();
        var (x, y) = s.Apply(0, 0);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void Direction_is_preserved()
    {
        var s = new StickSettings { InnerDeadzone = 0.1 };
        var (x, y) = s.Apply(0.6, 0.8); // 3-4-5 三角形方向
        double ratio = y / x;
        Assert.Equal(8.0 / 6.0, ratio, 5);
    }

    [Fact]
    public void Full_deflection_reaches_full_output()
    {
        var s = new StickSettings();
        var (x, _) = s.Apply(1, 0);
        Assert.Equal(1.0, x, 5);
    }
}

public class SimpleReportTests
{
    [Fact]
    public void Parses_simple_hid_report()
    {
        var report = new byte[12];
        report[0] = 0x3F;
        report[1] = 0x01; // Down
        report[2] = 0x52; // Plus(bit1=0x02) + Home(bit4=0x10) + L(bit6=0x40)
        // 摇杆为每摇杆 4 字节：16 位水平 + 16 位垂直（dekuNukem simple HID：
        // horizontal = d[0]|d[1]<<8，vertical = d[2]|d[3]<<8）
        report[4] = 0x34; report[5] = 0x12;  // L X = 0x1234
        report[6] = 0x78; report[7] = 0x56;  // L Y = 0x5678
        report[8] = 0x00; report[9] = 0x80;  // R X = 0x8000
        report[10] = 0xFF; report[11] = 0x0F; // R Y = 0x0FFF

        var ok = InputReportParser.TryParseSimple(report, out var state);

        Assert.True(ok);
        Assert.True(state.Buttons.HasFlag(ProButtons.Down));
        Assert.True(state.Buttons.HasFlag(ProButtons.Plus));
        Assert.True(state.Buttons.HasFlag(ProButtons.Home));
        Assert.True(state.Buttons.HasFlag(ProButtons.L));
        Assert.False(state.Buttons.HasFlag(ProButtons.Zl));
        Assert.Equal(0x1234, state.LeftStick.X);
        Assert.Equal(0x5678, state.LeftStick.Y);
        Assert.Equal(0x8000, state.RightStick.X);
        Assert.Equal(0x0FFF, state.RightStick.Y);
    }
}

public class ControllerProfileTests
{
    [Fact]
    public void BindingOf_returns_null_when_slot_unbound()
    {
        // 回归：FirstOrDefault().Key 是非空枚举，default=None，"is {}" 恒真——
        // 未绑定必须显式返回 null
        var profile = new ControllerProfile();
        profile.Bind(XboxButtons.A, null);
        Assert.Null(profile.BindingOf(XboxButtons.A));
    }

    [Fact]
    public void BindingOf_returns_bound_button()
    {
        var profile = new ControllerProfile();
        Assert.Equal(ProButtons.A, profile.BindingOf(XboxButtons.B)); // Nintendo 布局默认 A→B

        profile.Bind(XboxButtons.B, ProButtons.Zr);
        Assert.Equal(ProButtons.Zr, profile.BindingOf(XboxButtons.B));
    }

    [Fact]
    public void Bind_replaces_previous_binding_for_slot()
    {
        var profile = new ControllerProfile();
        profile.Bind(XboxButtons.A, ProButtons.Plus);
        Assert.Equal(ProButtons.Plus, profile.BindingOf(XboxButtons.A));
        // 旧绑定（Nintendo 布局 B→A 槽）已被移除
        Assert.DoesNotContain(profile.ButtonMap, kv => kv.Key == ProButtons.B && kv.Value == XboxButtons.A);
    }
}
