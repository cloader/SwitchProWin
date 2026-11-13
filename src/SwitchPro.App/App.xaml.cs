using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using SwitchPro.Engine;

namespace SwitchPro.App;

/// <summary>
/// 应用宿主：单实例、引擎生命周期、托盘图标、窗口显隐。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "SwitchPro.App.SingleInstance";

    private Mutex? _mutex;
    private SwitchProEngine? _engine;
    private TaskbarIcon? _tray;
    private MainWindow? _mainWindow;
    private bool _exitRequested;

    /// <summary>全局引擎实例（供向导等子窗口访问）。</summary>
    public SwitchProEngine Engine => _engine!;

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchPro", "log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例
        _mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Switch Pro 控制软件已在运行（请查看系统托盘）。", "SwitchPro",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 全局异常兜底
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog($"未处理异常: {args.Exception}");
            MessageBox.Show($"发生未处理异常：{args.Exception.Message}", "SwitchPro",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // 后台线程（读循环等）未处理异常会直接杀死进程：至少留一份日志便于排查
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            FileLog($"未处理异常(后台线程): {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            FileLog($"未观察任务异常: {args.Exception}");

        // 引擎
        _engine = new SwitchProEngine();
        _engine.Log += FileLog;
        _engine.LowBatteryWarning += player => Dispatcher.BeginInvoke(() =>
            _tray?.ShowBalloonTip("SwitchPro", $"手柄 {player} 电量低，请及时充电。", BalloonIcon.Warning));
        _engine.Start();

        // 托盘
        _tray = new TaskbarIcon
        {
            ToolTipText = "Switch Pro 控制软件",
            IconSource = CreateAppIcon(),
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        _tray.MakeContextMenu(ShowMainWindow, ExitApplication);

        // 主窗口（隐藏启动，从托盘呼出）
        _mainWindow = new MainWindow(_engine);
        _mainWindow.HideToTrayRequested += () => _tray?.ShowBalloonTip("SwitchPro", "已最小化到托盘，双击托盘图标打开。", BalloonIcon.Info);
        ShowMainWindow();

        FileLog("应用已启动");
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        if (_mainWindow is not null) _mainWindow.ForceClose = true; // 让 OnClosing 走真实关闭分支
        _tray?.Dispose();
        _tray = null;
        _mainWindow?.Close();
        _engine?.Dispose();
        FileLog("应用已退出");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exitRequested)
        {
            // 异常路径退出也要尽量清理
            _tray?.Dispose();
            _engine?.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>运行时生成托盘图标（手柄轮廓），避免携带二进制资源。</summary>
    private static ImageSource CreateAppIcon()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x36));
        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x7E));
        brush.Freeze();
        accent.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 主体
            dc.DrawRoundedRectangle(brush, null, new Rect(4, 11, 24, 11), 5, 5);
            // 左右握把
            dc.DrawEllipse(brush, null, new Point(8, 20), 5, 5);
            dc.DrawEllipse(brush, null, new Point(24, 20), 5, 5);
            // 面键（右上四点）
            foreach (var (x, y) in new[] { (21, 13), (25, 13), (23, 11), (23, 15) })
                dc.DrawEllipse(accent, null, new Point(x, y), 1.4, 1.4);
            // 十字键（左上）
            dc.DrawRoundedRectangle(accent, null, new Rect(8, 12.5, 5, 2), 1, 1);
            dc.DrawRoundedRectangle(accent, null, new Rect(9.5, 11, 2, 5), 1, 1);
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static readonly object _fileLogLock = new();

    /// <summary>轻量文件日志（超出 256KB 重写）。多线程写入加锁，避免并发 Append 抛 IO 异常丢日志。</summary>
    private static void FileLog(string message)
    {
        lock (_fileLogLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                var fi = new FileInfo(LogFilePath);
                if (fi.Exists && fi.Length > 256 * 1024)
                    File.WriteAllText(LogFilePath, line + Environment.NewLine);
                else
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}

/// <summary>TaskbarIcon 的托盘菜单构建扩展。</summary>
internal static class TrayExtensions
{
    public static void MakeContextMenu(this TaskbarIcon tray,
        Action showWindow, Action exit)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var show = new System.Windows.Controls.MenuItem { Header = "打开控制面板" };
        show.Click += (_, _) => showWindow();
        menu.Items.Add(show);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var quit = new System.Windows.Controls.MenuItem { Header = "退出" };
        quit.Click += (_, _) => exit();
        menu.Items.Add(quit);

        tray.ContextMenu = menu;
    }
}
