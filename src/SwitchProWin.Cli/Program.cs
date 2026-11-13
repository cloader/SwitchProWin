using SwitchProWin.Engine;
using SwitchProWin.Mapper;
using SwitchProWin.Protocol;

namespace SwitchProWin.Cli;

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
        Loc.Apply(AppConfig.Load().Language); // 与 GUI 一致：语言来自配置文件（auto 按系统语言）
        Console.WriteLine(Loc.S("cli.banner"));
        Console.WriteLine(Loc.F("cli.vigemStatus", QueryViGEmBus()));

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
        Console.WriteLine(Loc.S("cli.connectHint"));
        Console.WriteLine(Loc.S("cli.keys"));
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

        Console.WriteLine(Loc.S("cli.exiting"));
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
            Console.WriteLine(Loc.S("cli.rumbleOffline"));
            return;
        }
        foreach (var s in snapshot)
        {
            try { s.SetRumble(large, small); }
            catch (Exception ex) { Console.WriteLine(Loc.F("cli.rumbleFailed", ex.Message)); }
        }
        Console.WriteLine(Loc.F("cli.rumbleSent", large, small, snapshot.Length));
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
        Console.WriteLine(Loc.S("cli.testStart"));
        try
        {
            using var pad = new SwitchProWin.ViGEm.VirtualXboxController();
            pad.RumbleReceived += (l, s) => { /* 静默 */ };
            // 中位状态保持
            pad.Update(Xbox360State.Empty);
            Thread.Sleep(3000);
            Console.WriteLine(Loc.S("cli.testDone"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(Loc.F("cli.testFailed", ex.Message));
            Console.WriteLine(Loc.S("cli.testHint"));
        }
    }

    private static string QueryViGEmBus()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            if (key is null) return Loc.S("cli.vigemMissing");
            var imagePath = key.GetValue("ImagePath") as string ?? "";
            var version = key.GetValue("Version") as string ?? Loc.S("cli.vigemUnknownVersion");
            return Loc.F("cli.vigemInstalled", version);
        }
        catch
        {
            return Loc.S("cli.vigemQueryFailed");
        }
    }
}
