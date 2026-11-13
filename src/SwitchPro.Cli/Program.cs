using SwitchPro.Engine;
using SwitchPro.Mapper;
using SwitchPro.Protocol;

namespace SwitchPro.Cli;

/// <summary>
/// M1 验证控制台：
///  - 无手柄：打印 ViGEmBus 状态并创建一只虚拟 X360 手柄供 joy.cpl 检查
///  - 有手柄：完成初始化 + SPI 校准 + 实时状态显示 + 震动回传
/// 退出：Ctrl+C 或按 Q。
/// </summary>
internal static class Program
{
    private static readonly string[] ButtonNames =
    [
        nameof(ProButtons.A), nameof(ProButtons.B), nameof(ProButtons.X), nameof(ProButtons.Y),
        nameof(ProButtons.L), nameof(ProButtons.R), nameof(ProButtons.Zl), nameof(ProButtons.Zr),
        nameof(ProButtons.Minus), nameof(ProButtons.Plus), nameof(ProButtons.Home), nameof(ProButtons.Capture),
        nameof(ProButtons.LStickPress), nameof(ProButtons.RStickPress),
        nameof(ProButtons.Up), nameof(ProButtons.Down), nameof(ProButtons.Left), nameof(ProButtons.Right),
    ];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== SwitchProCli — Switch Pro 手柄驱动 (M1) ===");
        Console.WriteLine($"ViGEmBus 驱动: {QueryViGEmBus()}");

        using var manager = new DeviceManager();
        var sessions = new List<ProControllerSession>();

        manager.Log += m => Console.WriteLine(m);
        manager.ControllerArrived += s =>
        {
            lock (sessions) sessions.Add(s);
            s.StateReceived += st => Render(s, st);
        };
        manager.ControllerRemoved += s =>
        {
            lock (sessions) sessions.Remove(s);
        };

        manager.Start();

        Console.WriteLine();
        Console.WriteLine("将手柄通过 USB/蓝牙连接（自动接入）");
        Console.WriteLine("按键：1=低频马达 2=高频马达 3=双马达 0=停止  V=纯虚拟手柄测试  Q=退出");
        Console.WriteLine();

        while (true)
        {
            ConsoleKey key;
            try
            {
                key = Console.ReadKey(intercept: true).Key;
            }
            catch (InvalidOperationException)
            {
                // 输入被重定向（如管道运行）：退化为按行读取
                var line = Console.ReadLine();
                if (line is null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) break;
                HandleCommand(line.Trim(), sessions);
                continue;
            }
            if (key is ConsoleKey.Q) break;
            HandleCommand(key.ToString(), sessions);
        }

        Console.WriteLine("退出中...");
        return 0;
    }

    private static void HandleCommand(string cmd, List<ProControllerSession> sessions)
    {
        switch (cmd.ToUpperInvariant())
        {
            case "D1" or "1" or "NUMPAD1":
                SendRumble(sessions, 220, 0);
                break;
            case "D2" or "2" or "NUMPAD2":
                SendRumble(sessions, 0, 220);
                break;
            case "D3" or "3" or "NUMPAD3":
                SendRumble(sessions, 220, 220);
                break;
            case "D0" or "0" or "NUMPAD0":
                SendRumble(sessions, 0, 0);
                break;
            case "V":
                TestVirtualPad();
                break;
        }
    }

    private static void SendRumble(List<ProControllerSession> sessions, byte large, byte small)
    {
        ProControllerSession[] snapshot;
        lock (sessions) snapshot = sessions.ToArray();
        if (snapshot.Length == 0)
        {
            Console.WriteLine("[震动] 手柄不在线");
            return;
        }
        foreach (var s in snapshot)
        {
            try { s.SetRumble(large, small); }
            catch (Exception ex) { Console.WriteLine($"[震动] 失败: {ex.Message}"); }
        }
        Console.WriteLine($"[震动] large={large} small={small} x{snapshot.Length}");
    }

    private static void Render(ProControllerSession session, ProControllerState state)
    {
        var pressed = ButtonNames.Where(n => Enum.TryParse<ProButtons>(n, out var b) && state.Buttons.HasFlag(b));
        int lx = state.LeftStick.X, ly = state.LeftStick.Y;
        int rx = state.RightStick.X, ry = state.RightStick.Y;

        string line =
            $"BAT={state.BatteryLevel}{(state.Charging ? "+" : " ")} " +
            $"L=({lx,4},{ly,4}) R=({rx,4},{ry,4}) " +
            $"[{string.Join("+", pressed)}]   ";

        lock (Console.Out)
        {
            try
            {
                int width = Math.Min(Console.WindowWidth - 1, line.Length);
                Console.CursorLeft = 0;
                Console.Write(line[..width]);
            }
            catch
            {
                // 输出被重定向或窗口过小：直接整行输出
                Console.WriteLine(line);
            }
        }
    }

    private static void TestVirtualPad()
    {
        Console.WriteLine("[测试] 创建虚拟 X360 手柄 3 秒（应能在 joy.cpl / gamepad-tester 看到）...");
        try
        {
            using var pad = new SwitchPro.ViGEm.VirtualXboxController();
            pad.RumbleReceived += (l, s) => { /* 静默 */ };
            // 中位状态保持
            pad.Update(Xbox360State.Empty);
            Thread.Sleep(3000);
            Console.WriteLine("[测试] 完成，虚拟手柄已移除");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[测试] 失败: {ex.Message}");
            Console.WriteLine("       请确认已安装 ViGEmBus 驱动: https://github.com/nefarius/ViGEmBus/releases");
        }
    }

    private static string QueryViGEmBus()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            if (key is null) return "未安装";
            var imagePath = key.GetValue("ImagePath") as string ?? "";
            var version = key.GetValue("Version") as string ?? "未知版本";
            return $"已安装 ({version})";
        }
        catch
        {
            return "查询失败";
        }
    }
}
