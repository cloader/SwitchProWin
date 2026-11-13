using System.Globalization;

namespace SwitchProWin.Engine;

/// <summary>
/// 极简本地化：zh/en 两张字符串表，中文为回退基准。
/// 语言由 <see cref="AppConfig.Language"/> 驱动（auto/zh/en，auto 按系统 UI 语言解析）；
/// 在 <see cref="AppConfig.Load"/> 时应用，切换语言后重启生效。
/// 用法：代码 <c>Loc.S("key")</c> / <c>Loc.F("key", arg)</c>；XAML 用 App 层的 <c>{loc:Loc key}</c>。
/// </summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string> _table = StringsZh.Table;

    /// <summary>当前生效语言："zh" 或 "en"（auto 已解析）。</summary>
    public static string Language { get; private set; } = "zh";

    /// <summary>由配置值（auto/zh/en/其他）解析并应用语言。</summary>
    public static void Apply(string? configured)
    {
        Language = (configured ?? "auto") switch
        {
            "zh" => "zh",
            "en" => "en",
            _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh"
                : "en",
        };
        _table = Language == "en" ? StringsEn.Table : StringsZh.Table;
    }

    /// <summary>取键对应文本：当前语言 → 中文回退 → 键本身（缺失键直接可见，便于发现漏翻）。</summary>
    public static string S(string key) =>
        _table.TryGetValue(key, out var s) ? s : StringsZh.Table.GetValueOrDefault(key, key);

    /// <summary>取键并格式化（{0} 占位符）。</summary>
    public static string F(string key, params object?[] args) => string.Format(S(key), args);

    /// <summary>测试辅助：恢复默认 zh 状态。</summary>
    internal static void Reset()
    {
        Language = "zh";
        _table = StringsZh.Table;
    }
}
