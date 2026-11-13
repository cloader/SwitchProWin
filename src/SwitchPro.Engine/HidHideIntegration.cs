using Nefarius.Drivers.HidHide;

namespace SwitchPro.Engine;

/// <summary>
/// HidHide（Nefarius）防双输入集成：把实体手柄从游戏视角隐藏，
/// 游戏只看到虚拟 X360 手柄；本软件自身通过白名单保持读取权限。
/// 未安装 HidHide 驱动时全部操作静默跳过。
/// </summary>
public static class HidHideIntegration
{
    /// <summary>HidHide 驱动是否可用。</summary>
    public static bool IsOperational()
    {
        try
        {
            var service = new HidHideControlService();
            return service.IsInstalled && service.IsOperational;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把设备实例加入隐藏列表，并确保本程序在白名单内。
    /// </summary>
    /// <returns>true = 已配置成功；false = HidHide 不可用或配置失败。</returns>
    public static bool EnsureHidden(string devicePath, string? whitelistExecutable, Action<string> log)
    {
        try
        {
            var service = new HidHideControlService();
            if (!service.IsInstalled || !service.IsOperational)
            {
                log("HidHide 未安装，跳过防双输入（可从 github.com/nefarius/HidHide/releases 安装）");
                return false;
            }

            // 1. 白名单本程序（否则隐藏后自己也无法读取设备）
            if (!string.IsNullOrEmpty(whitelistExecutable))
            {
                bool whitelisted = service.ApplicationPaths.Any(
                    p => string.Equals(p, whitelistExecutable, StringComparison.OrdinalIgnoreCase));
                if (!whitelisted)
                {
                    service.AddApplicationPath(whitelistExecutable, false);
                    log($"HidHide 白名单已添加: {whitelistExecutable}");
                }
            }

            // 2. 隐藏实体设备实例
            var instanceId = ToInstanceId(devicePath);
            if (!service.BlockedInstanceIds.Contains(instanceId, StringComparer.OrdinalIgnoreCase))
            {
                service.AddBlockedInstanceId(instanceId);
                log($"HidHide 已隐藏实体设备: {instanceId}");
            }

            if (!service.IsActive)
            {
                service.IsActive = true;
                log("HidHide 已激活（游戏将看不到实体手柄）");
            }
            return true;
        }
        catch (Exception ex)
        {
            log($"HidHide 配置失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>解除全部隐藏（设置页"恢复显示"用）。</summary>
    public static void UnhideAll(Action<string> log)
    {
        try
        {
            var service = new HidHideControlService();
            service.ClearBlockedInstancesList();
            service.IsActive = false;
            log("HidHide 已解除全部隐藏");
        }
        catch (Exception ex)
        {
            log($"HidHide 操作失败: {ex.Message}");
        }
    }

    /// <summary>设备路径 → 设备实例 ID："\\?\hid#a#b#c" → "HID\A\B\C"。</summary>
    public static string ToInstanceId(string devicePath)
    {
        var s = devicePath;
        if (s.StartsWith(@"\\?\"))
            s = s[4..];
        return s.ToUpperInvariant().Replace('#', '\\');
    }
}
