using System.Windows.Markup;
using SwitchProWin.Engine;

namespace SwitchProWin.App.Localization;

/// <summary>
/// XAML 本地化取值：用法 <c>Title="{loc:Loc win.title}"</c>。
/// 语言在启动时由 <see cref="Loc"/> 解析，切换语言后重启生效，因此无需动态刷新机制。
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.S(Key);
}
