using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SwitchProWin.Engine;

/// <summary>前台进程查询（按游戏自动切配置用）。</summary>
internal static class ForegroundProcess
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>当前前台进程名（小写、不含 .exe）；失败返回 null。</summary>
    public static string? Name()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch
        {
            return null; // 进程已退出 / 权限
        }
    }
}
