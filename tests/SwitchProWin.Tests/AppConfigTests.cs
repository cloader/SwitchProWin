using SwitchProWin.Engine;
using SwitchProWin.Mapper;
using SwitchProWin.Protocol;
using Xunit;

namespace SwitchProWin.Tests;

[Collection("localization")]
public class AppConfigTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"switchpro-test-{Guid.NewGuid():N}");

    public AppConfigTests()
    {
        Loc.Reset(); // 固定 zh 基准（"默认"断言依赖它）；与 LocalizationTests 同集合串行，防静态语言状态互串
        AppConfig.ConfigDirOverride = _tempDir;
    }

    [Fact]
    public void Save_then_load_roundtrips_settings()
    {
        var config = new AppConfig
        {
            NintendoLayout = false,
            LeftDeadzone = 0.15,
            RightDeadzone = 0.22,
            LeftCurve = StickCurve.Exponential,
            RightCurve = StickCurve.Cubic,
            RumbleMaxAmplitude = 0.45f,
            Autostart = true,
        };
        config.Save();

        var loaded = AppConfig.Load();
        Assert.False(loaded.NintendoLayout);
        Assert.Equal(0.15, loaded.LeftDeadzone);
        Assert.Equal(0.22, loaded.RightDeadzone);
        Assert.Equal(StickCurve.Exponential, loaded.LeftCurve);
        Assert.Equal(StickCurve.Cubic, loaded.RightCurve);
        Assert.Equal(0.45f, loaded.RumbleMaxAmplitude);
        Assert.True(loaded.Autostart);
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var loaded = AppConfig.Load();
        Assert.True(loaded.NintendoLayout);
        Assert.Equal(RumbleEncoder.DefaultMaxAmplitude, loaded.RumbleMaxAmplitude);
    }

    [Fact]
    public void Corrupted_file_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), "{ not valid json !!!");
        var loaded = AppConfig.Load();
        Assert.True(loaded.NintendoLayout);
    }

    [Fact]
    public void GetActive_falls_back_to_first_when_name_missing()
    {
        var config = new AppConfig();
        config.EnsureProfiles();
        config.Profiles.Add(new ControllerProfile { Name = "FPS" });
        config.ActiveProfileName = "不存在的配置";
        Assert.Equal("默认", config.GetActive().Name);
    }

    [Fact]
    public void EnsureProfiles_migrates_legacy_fields_once()
    {
        var config = new AppConfig
        {
            NintendoLayout = false,
            LeftDeadzone = 0.2,
            LeftCurve = StickCurve.Linear,
        };
        Assert.Empty(config.Profiles);

        config.EnsureProfiles();
        var p = Assert.Single(config.Profiles);
        // NintendoLayout=false → 迁移为 Xbox 布局按键表（A→A 不互换）
        Assert.Equal(XboxButtons.A, p.ButtonMap[ProButtons.A]);
        Assert.Equal(0.2, p.LeftDeadzone);
        Assert.Equal(StickCurve.Linear, p.LeftCurve);

        // 已有 Profiles 时不再重复迁移（改回 Nintendo 旧字段也不影响已迁移的表）
        config.NintendoLayout = true;
        config.EnsureProfiles();
        Assert.Single(config.Profiles);
        Assert.Equal(XboxButtons.A, config.GetActive().ButtonMap[ProButtons.A]);
    }

    [Fact]
    public void ApplyTo_maps_all_mapping_fields()
    {
        var config = new AppConfig
        {
            NintendoLayout = false,
            LeftDeadzone = 0.2,
            RightDeadzone = 0.3,
            LeftOuterDeadzone = 0.08,
            RightOuterDeadzone = 0.09,
            LeftCurve = StickCurve.Cubic,
            RightCurve = StickCurve.Linear,
        };
        var profile = new MappingProfile();
        config.ApplyTo(profile);

        Assert.False(profile.NintendoLayout);
        Assert.Equal(0.2, profile.LeftStick.InnerDeadzone);
        Assert.Equal(0.3, profile.RightStick.InnerDeadzone);
        Assert.Equal(0.08, profile.LeftStick.OuterDeadzone);
        Assert.Equal(0.09, profile.RightStick.OuterDeadzone);
        Assert.Equal(StickCurve.Cubic, profile.LeftStick.Curve);
        Assert.Equal(StickCurve.Linear, profile.RightStick.Curve);
    }

    public void Dispose()
    {
        AppConfig.ConfigDirOverride = null;
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }
}
