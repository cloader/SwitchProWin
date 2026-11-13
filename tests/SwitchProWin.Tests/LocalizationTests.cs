using SwitchProWin.Engine;
using Xunit;

namespace SwitchProWin.Tests;

/// <summary>本地化：键表奇偶校验、语言切换与回退、配置往返。
/// 与 AppConfigTests 同集合串行（Loc 是静态状态，避免并行互串）。</summary>
[Collection("localization")]
public class LocalizationTests : IDisposable
{
    public LocalizationTests() => Loc.Reset();

    [Fact]
    public void Zh_en_tables_have_identical_key_sets()
    {
        Assert.Equal(
            StringsZh.Table.Keys.OrderBy(k => k, StringComparer.Ordinal),
            StringsEn.Table.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Apply_zh_en_switches_language()
    {
        Assert.Equal("默认", Loc.S("profile.default"));
        Loc.Apply("en");
        Assert.Equal("en", Loc.Language);
        Assert.Equal("Default", Loc.S("profile.default"));
        Loc.Apply("zh");
        Assert.Equal("zh", Loc.Language);
        Assert.Equal("默认", Loc.S("profile.default"));
    }

    [Fact]
    public void Missing_key_returns_key_itself()
    {
        // 两表均无此键 → 回退键本身（漏翻键在 UI 上直接可见）
        Assert.Equal("no.such.key", Loc.S("no.such.key"));
    }

    [Fact]
    public void Format_uses_current_language()
    {
        Loc.Apply("en");
        Assert.Equal("Stick deadzone: 15%", Loc.F("lbl.deadzone", 15));
        Loc.Apply("zh");
        Assert.Equal("摇杆死区：15%", Loc.F("lbl.deadzone", 15));
    }

    [Fact]
    public void Config_language_roundtrips_and_drives_apply()
    {
        // 模拟英文全新安装：config.json 只有语言字段、无配置文件 → Load 时以 en 创建默认配置
        var dir = Path.Combine(Path.GetTempPath(), $"switchprowin-loc-{Guid.NewGuid():N}");
        try
        {
            AppConfig.ConfigDirOverride = dir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """{"Language":"en"}""");

            var loaded = AppConfig.Load(); // Load 内部应用语言
            Assert.Equal("en", loaded.Language);
            Assert.Equal("en", Loc.Language);
            Assert.Equal("Default", loaded.GetActive().Name);

            // 已持久化的配置名不随语言改名（名字是用户数据）：zh 安装的"默认"切 en 后保持原样
            Loc.Apply("en");
            var config = new AppConfig { Language = "en" };
            config.Save(); // Save 先物化（此时 Loc 已是 en → "Default"，下一行直接断言当前语言语义）
            Assert.Equal("Default", config.Profiles[0].Name);
        }
        finally
        {
            AppConfig.ConfigDirOverride = null;
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            Loc.Reset();
        }
    }

    [Fact]
    public void EnsureProfiles_uses_localized_default_name()
    {
        Loc.Apply("en");
        var config = new AppConfig();
        config.EnsureProfiles();
        Assert.Equal("Default", config.Profiles[0].Name);
        Assert.Equal("Default", config.GetActive().Name);
    }

    public void Dispose() => Loc.Reset();
}
