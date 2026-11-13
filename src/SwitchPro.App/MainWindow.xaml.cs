using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchPro.App.Controls;
using SwitchPro.Engine;
using SwitchPro.Mapper;
using SwitchPro.Protocol;

namespace SwitchPro.App;

public partial class MainWindow : Window
{
    private readonly SwitchProEngine _engine;
    private readonly ObservableCollection<DeviceCard> _cards = new();
    private readonly DispatcherTimer _deviceTimer;
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _captureTimer;

    // 槽位：Xbox 目标键 -> 按钮
    private readonly Dictionary<XboxButtons, Button> _slots = new();

    // 捕获状态
    private Button? _captureSlot;
    private XboxButtons _captureTarget;
    private ProButtons _captureInitial;
    private DateTime _captureStart;
    private int _captureSessionId;

    private static readonly StickCurve[] CurveValues =
        [StickCurve.Linear, StickCurve.GentleQuad, StickCurve.Exponential, StickCurve.AggressiveExponential, StickCurve.Cubic];
    private static readonly string[] CurveNames =
        ["线性", "平缓 (1.4)", "指数 (2.0)", "强指数 (3.0)", "平滑 (3-2-1)"];

    private readonly List<SessionItem> _liveItems = new();
    private bool _loading = true;
    private bool _balloonShown;
    private int _logLength;

    public event Action? HideToTrayRequested;
    public bool ForceClose { get; set; }

    public MainWindow(SwitchProEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        BuildSlots();
        foreach (var name in CurveNames)
        {
            LeftCurveBox.Items.Add(name);
            RightCurveBox.Items.Add(name);
        }

        RefreshProfileBox();
        LoadProfileToControls();

        DeviceList.ItemsSource = _cards; // 卡片列表绑定（集合变更自动刷新）
        RefreshDeviceCards();
        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _deviceTimer.Tick += (_, _) => UpdateDeviceCards();
        _deviceTimer.Start();

        RefreshLiveDevices();
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _liveTimer.Tick += (_, _) => UpdateLiveView();
        _liveTimer.Start();

        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _captureTimer.Tick += (_, _) => PollCapture();

        // 日志：订阅后回放引擎启动以来的历史（设备可能在窗口构造前已初始化）
        _engine.Log += AppendLog;
        foreach (var entry in _engine.RecentLogs)
            AppendLog(entry);
        AppendLog("控制面板已打开");

        _loading = false;
    }

    // ================= 槽位构建 =================

    private void BuildSlots()
    {
        void Add(XboxButtons target, string header, Panel parent)
        {
            var label = new TextBlock
            {
                Text = header,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x96)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 2),
            };
            var button = new Button
            {
                Tag = target,
                Height = 30,
            };
            button.Click += Slot_Click;

            // 右键：切换连发
            var menu = new ContextMenu();
            var turboItem = new MenuItem { Header = "连发" };
            turboItem.Click += (_, _) => ToggleTurbo(target);
            menu.Items.Add(turboItem);
            button.ContextMenu = menu;

            parent.Children.Add(label);
            parent.Children.Add(button);
            _slots[target] = button;
        }

        Add(XboxButtons.LeftTrigger, "LT", PanelLT);
        Add(XboxButtons.LeftShoulder, "LB", PanelLB);
        Add(XboxButtons.RightTrigger, "RT", PanelRT);
        Add(XboxButtons.RightShoulder, "RB", PanelRB);
        Add(XboxButtons.X, "X", PanelX);
        Add(XboxButtons.A, "A", PanelA);
        Add(XboxButtons.Y, "Y", PanelY);
        Add(XboxButtons.B, "B", PanelB);
        Add(XboxButtons.Back, "Back", PanelBack);
        Add(XboxButtons.Start, "Start", PanelStart);
        Add(XboxButtons.Guide, "Guide", PanelGuide);
        Add(XboxButtons.LeftThumb, "LS（左摇杆按下）", PanelLS);
        Add(XboxButtons.RightThumb, "RS（右摇杆按下）", PanelRS);
        Add(XboxButtons.DpadLeft, "十字 ←", PanelDLeft);
        Add(XboxButtons.DpadUp, "十字 ↑", PanelDUp);
        Add(XboxButtons.DpadDown, "十字 ↓", PanelDDown);
        Add(XboxButtons.DpadRight, "十字 →", PanelDRight);
    }

    private static string ProName(ProButtons b) => b switch
    {
        ProButtons.A => "A", ProButtons.B => "B", ProButtons.X => "X", ProButtons.Y => "Y",
        ProButtons.L => "L", ProButtons.R => "R", ProButtons.Zl => "ZL", ProButtons.Zr => "ZR",
        ProButtons.Minus => "-", ProButtons.Plus => "+",
        ProButtons.LStickPress => "左摇杆按下", ProButtons.RStickPress => "右摇杆按下",
        ProButtons.Home => "HOME", ProButtons.Capture => "CAPTURE",
        ProButtons.Up => "十字↑", ProButtons.Down => "十字↓",
        ProButtons.Left => "十字←", ProButtons.Right => "十字→",
        _ => b.ToString(),
    };

    /// <summary>槽位右键：切换该槽位绑定键的连发。</summary>
    private void ToggleTurbo(XboxButtons target)
    {
        var profile = _engine.ActiveProfile;
        var bound = profile.BindingOf(target);
        if (bound is not { } pro)
        {
            AppendLog($"[连发] {target} 槽位未绑定按键，先绑定再开启连发");
            return;
        }
        var name = pro.ToString();
        if (profile.TurboButtons.Contains(name))
        {
            profile.TurboButtons.Remove(name);
            AppendLog($"[连发] 关闭 {ProName(pro)}");
        }
        else
        {
            profile.TurboButtons.Add(name);
            AppendLog($"[连发] 开启 {ProName(pro)}（按住时 ~10Hz）");
        }
        _engine.ApplyAndSave();
        RefreshSlotLabels();
    }

    private void RefreshSlotLabels()
    {
        var profile = _engine.ActiveProfile;
        foreach (var (target, button) in _slots)
        {
            var bound = profile.BindingOf(target);
            bool turbo = bound is { } b && profile.TurboButtons.Contains(b.ToString());
            button.Content = bound is { } pb
                ? $"按键 {ProName(pb)}{(turbo ? " [连发]" : "")}"
                : "未映射";
        }
    }

    // ================= 槽位捕获 =================

    private void Slot_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var target = (XboxButtons)button.Tag;

        if (_captureSlot is not null) CancelCapture();

        var session = SelectedSession;
        if (session is null)
        {
            MessageBox.Show("请先连接手柄再进行按键映射。", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _captureSlot = button;
        _captureTarget = target;
        _captureInitial = session.LatestState?.Buttons ?? ProButtons.None;
        _captureStart = DateTime.UtcNow;
        _captureSessionId = session.PlayerIndex;
        button.Content = "等待输入…（Esc 取消）";
        _captureTimer.Start();
    }

    private void PollCapture()
    {
        if (_captureSlot is null) return;

        // 超时 5 秒
        if ((DateTime.UtcNow - _captureStart).TotalSeconds > 5)
        {
            CancelCapture();
            return;
        }

        var session = SelectedSession;
        if (session is null || session.PlayerIndex != _captureSessionId) return;
        var state = session.LatestState;
        if (state is null) return;

        // 找出捕获开始后新按下的键（取最低位）
        var newly = state.Value.Buttons & ~_captureInitial;
        if (newly == 0) return;

        ProButtons captured = 0;
        foreach (ProButtons flag in Enum.GetValues<ProButtons>())
        {
            if (newly.HasFlag(flag) && flag != ProButtons.None)
            {
                captured = flag;
                break;
            }
        }
        if (captured == 0) return;

        _engine.ActiveProfile.Bind(_captureTarget, captured);
        _engine.ApplyAndSave();
        RefreshSlotLabels();
        AppendLog($"映射：{_captureTarget} ← {ProName(captured)}");

        _captureSlot = null;
        _captureTimer.Stop();
    }

    private void CancelCapture()
    {
        _captureSlot = null;
        _captureTimer.Stop();
        RefreshSlotLabels();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && _captureSlot is not null)
        {
            CancelCapture();
            e.Handled = true;
        }
    }

    // ================= 配置文件 =================

    private void RefreshProfileBox()
    {
        string? selected = ProfileBox.SelectedItem as string;
        ProfileBox.ItemsSource = _engine.Config.Profiles.Select(p => p.Name).ToList();
        ProfileBox.SelectedItem = _engine.Config.ActiveProfileName;
        if (selected is not null && ProfileBox.SelectedItem is null)
            ProfileBox.SelectedIndex = 0;
    }

    // 体感激活键选项：(显示名, 配置值 null=始终)
    private static readonly (string Display, string? Value)[] GyroActivateOptions =
    [
        ("始终启用", null),
        ("按住 ZL", nameof(ProButtons.Zl)),
        ("按住 ZR", nameof(ProButtons.Zr)),
        ("按住 L", nameof(ProButtons.L)),
        ("按住 R", nameof(ProButtons.R)),
        ("按住 左摇杆", nameof(ProButtons.LStickPress)),
        ("按住 右摇杆", nameof(ProButtons.RStickPress)),
        ("按住 HOME", nameof(ProButtons.Home)),
    ];

    private void LoadProfileToControls()
    {
        var p = _engine.ActiveProfile;
        LeftDeadzoneSlider.Value = p.LeftDeadzone * 100;
        RightDeadzoneSlider.Value = p.RightDeadzone * 100;
        LeftCurveBox.SelectedIndex = Math.Max(0, Array.IndexOf(CurveValues, p.LeftCurve));
        RightCurveBox.SelectedIndex = Math.Max(0, Array.IndexOf(CurveValues, p.RightCurve));
        RumbleSlider.Value = p.RumbleMaxAmplitude * 100;
        GyroModeBox.SelectedIndex = (int)p.GyroMode;
        GyroSensSlider.Value = p.GyroSensitivity * 100;
        GyroInvertXCheck.IsChecked = p.GyroInvertX;
        GyroInvertYCheck.IsChecked = p.GyroInvertY;
        GyroSwapCheck.IsChecked = p.GyroSwapAxes;
        GyroAutoCalCheck.IsChecked = p.GyroAutoCalibrate;

        // 激活键下拉（首次填充）
        if (GyroActivateBox.Items.Count == 0)
            foreach (var (display, _) in GyroActivateOptions)
                GyroActivateBox.Items.Add(display);
        GyroActivateBox.SelectedIndex = Math.Max(0,
            Array.FindIndex(GyroActivateOptions, o => o.Value == p.GyroActivationButton));

        AutoSwitchBox.Text = string.Join(", ", p.AutoSwitchProcesses);

        IdleSleepBox.SelectedIndex = Math.Clamp(
            Array.IndexOf(IdleSleepMinutes, p.IdleSleepMinutes), 0, IdleSleepMinutes.Length - 1);

        FlickStickCheck.IsChecked = p.FlickStickEnabled;
        FlickSensSlider.Value = p.FlickStickSensitivity * 100;

        // 全局设置（设备页）
        (_engine.Config.JoyConMode == JoyConMode.Pair ? JoyConPairRadio : JoyConSingleRadio).IsChecked = true;
        ComboCycleCheck.IsChecked = _engine.Config.ComboProfileCycle;
        ComboRecenterCheck.IsChecked = _engine.Config.ComboGyroRecenter;
        // 开机自启以注册表为准（安装器任务也会直接写注册表）
        AutostartCheck.IsChecked = Autostart.IsSet();

        RefreshSlotLabels();
        UpdateMappingLabels();
    }

    /// <summary>自动切换进程编辑（LostFocus 保存）。</summary>
    private void AutoSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var list = AutoSwitchBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s =>
            {
                s = s.ToLowerInvariant().Trim();
                // 按后缀整体去除（TrimEnd 按字符集裁剪会误伤恰好以 x/e/. 结尾的进程名）
                return s.EndsWith(".exe", StringComparison.Ordinal) ? s[..^".exe".Length] : s;
            })
            .Distinct()
            .ToList();
        _engine.ActiveProfile.AutoSwitchProcesses = list;
        _engine.ApplyAndSave();
        AppendLog($"[自动切换] 「{_engine.ActiveProfile.Name}」绑定进程：{(list.Count > 0 ? string.Join(", ", list) : "无")}");
    }

    // ================= 配置导入/导出 =================

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出当前配置",
            Filter = "SwitchPro 配置 (*.json)|*.json",
            FileName = $"{_engine.ActiveProfile.Name}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_engine.ActiveProfile,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            AppendLog($"[配置] 已导出到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入配置",
            Filter = "SwitchPro 配置 (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            var imported = System.Text.Json.JsonSerializer.Deserialize<ControllerProfile>(json);
            if (imported is null)
            {
                MessageBox.Show("文件不是有效的配置。", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 名称冲突则加后缀
            var name = imported.Name;
            int suffix = 2;
            while (_engine.Config.Profiles.Any(p => p.Name == name))
                name = $"{imported.Name} ({suffix++})";
            imported.Name = name;

            _engine.Config.Profiles.Add(imported);
            _engine.SetActiveProfile(name);
            RefreshProfileBox();
            _loading = true;
            LoadProfileToControls();
            _loading = false;
            AppendLog($"[配置] 已导入「{name}」并激活");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ProfileBox.SelectedItem is string name && name != _engine.Config.ActiveProfileName)
        {
            _engine.SetActiveProfile(name);
            _loading = true;
            LoadProfileToControls();
            _loading = false;
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        CollectProfileFromControls();
        _engine.ApplyAndSave();
        AppendLog($"配置「{_engine.ActiveProfile.Name}」已保存");
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt.ShowDialog(this, "新建配置文件", $"配置{DateTime.Now:MMdd-HHmm}");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_engine.Config.Profiles.Any(p => p.Name == name))
        {
            MessageBox.Show("同名配置已存在。", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        CollectProfileFromControls(); // 先把当前 UI 状态落到当前配置
        _engine.AddProfile(name.Trim());
        RefreshProfileBox();
        _loading = true;
        LoadProfileToControls();
        _loading = false;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = _engine.Config.ActiveProfileName;
        if (MessageBox.Show($"确定删除配置「{name}」？", "SwitchPro", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_engine.DeleteProfile(name))
        {
            RefreshProfileBox();
            _loading = true;
            LoadProfileToControls();
            _loading = false;
        }
    }

    private void NintendoPreset_Click(object sender, RoutedEventArgs e)
    {
        _engine.ActiveProfile.UseLayoutPreset(nintendo: true);
        _engine.ApplyAndSave();
        RefreshSlotLabels();
    }

    private void XboxPreset_Click(object sender, RoutedEventArgs e)
    {
        _engine.ActiveProfile.UseLayoutPreset(nintendo: false);
        _engine.ApplyAndSave();
        RefreshSlotLabels();
    }

    // ================= 映射/摇杆/震动控件 =================

    private void Mapping_Changed(object sender, RoutedEventArgs e) => OnMappingChanged();
    private void Mapping_Changed(object sender, SelectionChangedEventArgs e) => OnMappingChanged();
    private void Mapping_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => OnMappingChanged();

    private void OnMappingChanged()
    {
        if (_loading) return;
        UpdateMappingLabels();
        CollectProfileFromControls();
        _engine.ApplyAndSave();
    }

    private void CollectProfileFromControls()
    {
        var p = _engine.ActiveProfile;
        p.LeftDeadzone = LeftDeadzoneSlider.Value / 100;
        p.RightDeadzone = RightDeadzoneSlider.Value / 100;
        if (LeftCurveBox.SelectedIndex >= 0) p.LeftCurve = CurveValues[LeftCurveBox.SelectedIndex];
        if (RightCurveBox.SelectedIndex >= 0) p.RightCurve = CurveValues[RightCurveBox.SelectedIndex];
        p.RumbleMaxAmplitude = (float)(RumbleSlider.Value / 100);

        p.GyroMode = (GyroMode)Math.Max(0, GyroModeBox.SelectedIndex);
        p.GyroSensitivity = GyroSensSlider.Value / 100;
        p.GyroInvertX = GyroInvertXCheck.IsChecked == true;
        p.GyroInvertY = GyroInvertYCheck.IsChecked == true;
        p.GyroSwapAxes = GyroSwapCheck.IsChecked == true;
        p.GyroAutoCalibrate = GyroAutoCalCheck.IsChecked == true;
        p.GyroActivationButton = GyroActivateBox.SelectedIndex >= 0
            ? GyroActivateOptions[Math.Clamp(GyroActivateBox.SelectedIndex, 0, GyroActivateOptions.Length - 1)].Value
            : null;

        p.FlickStickEnabled = FlickStickCheck.IsChecked == true;
        p.FlickStickSensitivity = FlickSensSlider.Value / 100;
    }

    private void JoyConMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _engine.SetJoyConMode(JoyConPairRadio.IsChecked == true ? JoyConMode.Pair : JoyConMode.Individual);
    }

    private void Combo_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _engine.Config.ComboProfileCycle = ComboCycleCheck.IsChecked == true;
        _engine.Config.ComboGyroRecenter = ComboRecenterCheck.IsChecked == true;
        try { _engine.Config.Save(); }
        catch (Exception ex) { AppendLog($"配置保存失败: {ex.Message}"); }
    }

    private void GyroCalibrate_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession;
        if (session is null)
        {
            MessageBox.Show("请先连接手柄。", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        session.CalibrateGyroNow();
        AppendLog("[体感] 手动校零（请保持手柄静止 1 秒）");
    }

    // ================= 摇杆校准向导 =================

    private void CalibrateLeft_Click(object sender, RoutedEventArgs e) => OpenCalibrationWizard(left: true);
    private void CalibrateRight_Click(object sender, RoutedEventArgs e) => OpenCalibrationWizard(left: false);

    private void OpenCalibrationWizard(bool left)
    {
        var session = SelectedSession;
        if (session is null)
        {
            MessageBox.Show("请先连接手柄再校准。", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new CalibrationWizard(session, left) { Owner = this }.ShowDialog();
        AppendLog($"[校准] {(left ? "左" : "右")}摇杆向导完成");
    }

    private void UpdateMappingLabels()
    {
        LeftDeadzoneLabel.Text = $"摇杆死区：{LeftDeadzoneSlider.Value:0}%";
        RightDeadzoneLabel.Text = $"摇杆死区：{RightDeadzoneSlider.Value:0}%";
        RumbleLabel.Text = $"{RumbleSlider.Value:0}%";
        GyroSensSlider.ToolTip = $"灵敏度 {GyroSensSlider.Value:0}%";
    }

    // ================= 振动测试 =================

    private DispatcherTimer? _rumbleStopTimer;

    /// <summary>振动测试：按当前强度上限发一次 500ms 脉冲，自动停止。</summary>
    private void RumbleTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var session = SelectedSession;
        if (session is null)
        {
            MessageBox.Show("请先连接手柄再进行振动测试。", "SwitchPro",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StopRumbleTestTimer();
        try
        {
            switch (btn.Tag as string)
            {
                case "low":
                    session.SetRumble(255, 0);
                    AppendLog($"[振动测试] 低频马达（强度 {RumbleSlider.Value:0}%）");
                    break;
                case "high":
                    session.SetRumble(0, 255);
                    AppendLog($"[振动测试] 高频马达（强度 {RumbleSlider.Value:0}%）");
                    break;
                case "both":
                    session.SetRumble(255, 255);
                    AppendLog($"[振动测试] 双马达（强度 {RumbleSlider.Value:0}%）");
                    break;
                case "stop":
                    session.SetRumble(0, 0);
                    return; // 手动停止，无需定时器
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"振动测试失败：{ex.Message}", "SwitchPro",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _rumbleStopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _rumbleStopTimer.Tick += (_, _) =>
        {
            _rumbleStopTimer.Stop();
            try { session.SetRumble(0, 0); }
            catch { /* 设备可能已断开 */ }
        };
        _rumbleStopTimer.Start();
    }

    private void StopRumbleTestTimer()
    {
        _rumbleStopTimer?.Stop();
        _rumbleStopTimer = null;
    }

    // ================= 设备页 =================

    private void RefreshDeviceCards()
    {
        _cards.Clear();
        foreach (var s in _engine.Devices.Sessions)
            _cards.Add(new DeviceCard(s));
        DeviceEmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDeviceCards();
    }

    private void UpdateDeviceCards()
    {
        foreach (var card in _cards)
            card.Refresh();
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = AutostartCheck.IsChecked == true;
        try
        {
            Autostart.Set(on);
            _engine.Config.Autostart = on;
            _engine.Config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置自启动失败：{ex.Message}", "SwitchPro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static readonly int[] IdleSleepMinutes = [0, 5, 10, 30, 60];

    private void IdleSleep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var minutes = IdleSleepMinutes[Math.Clamp(IdleSleepBox.SelectedIndex, 0, IdleSleepMinutes.Length - 1)];
        _engine.ActiveProfile.IdleSleepMinutes = minutes;
        _engine.ApplyAndSave();
    }

    // ================= 实时测试 =================

    private ProControllerSession? SelectedSession =>
        LiveDeviceBox.SelectedItem is SessionItem item ? item.Session : null;

    private void RefreshLiveDevices()
    {
        var selected = SelectedSession;
        _liveItems.Clear();
        foreach (var s in _engine.Devices.Sessions)
            _liveItems.Add(new SessionItem(s));
        LiveDeviceBox.ItemsSource = null;
        LiveDeviceBox.ItemsSource = _liveItems;
        LiveDeviceBox.DisplayMemberPath = nameof(SessionItem.Text);
        if (selected is not null)
        {
            var idx = _liveItems.FindIndex(i => ReferenceEquals(i.Session, selected));
            if (idx >= 0) LiveDeviceBox.SelectedIndex = idx;
        }
        else if (_liveItems.Count > 0)
        {
            LiveDeviceBox.SelectedIndex = 0;
        }
    }

    private void LiveDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 切换设备后取消进行中的捕获，避免映射到错误的手柄
        if (_captureSlot is not null) CancelCapture();
    }

    private void UpdateLiveView()
    {
        if (_liveItems.Count != _engine.Devices.Sessions.Count)
            RefreshLiveDevices();

        var session = SelectedSession;
        var state = session?.LatestState;
        if (state is null)
        {
            ControllerView.UpdateState(null, (0, 0), (0, 0));
            SetAxisText(LeftAxisLeft, LeftAxisUp, LeftAxisDown, LeftAxisRight, (0, 0));
            SetAxisText(RightAxisLeft, RightAxisUp, RightAxisDown, RightAxisRight, (0, 0));
            return;
        }
        var st = state.Value;

        var left = session!.Profile.NormalizeLeftStick(st.LeftStick);
        var right = session.Profile.NormalizeRightStick(st.RightStick);
        ControllerView.UpdateState(st, left, right);
        ControllerView.SetDeadzones(session.Profile.LeftStick.InnerDeadzone, session.Profile.RightStick.InnerDeadzone);

        SetAxisText(LeftAxisLeft, LeftAxisUp, LeftAxisDown, LeftAxisRight, left);
        SetAxisText(RightAxisLeft, RightAxisUp, RightAxisDown, RightAxisRight, right);
    }

    // 设备页每 500ms 刷新（含性能统计）

    private static void SetAxisText(TextBlock left, TextBlock up, TextBlock down, TextBlock right, (double X, double Y) n)
    {
        left.Text = $"X {n.X,5:F2}";
        up.Text = $"Y {n.Y,5:F2}";
        down.Text = $"   {Math.Max(0, -n.Y),4:F2}";
        right.Text = $"   {Math.Max(0, n.X),4:F2}";
    }

    // ================= 日志 =================

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}" + Environment.NewLine);
        _logLength++;
        if (_logLength > 1000)
        {
            LogBox.Clear();
            _logLength = 0;
        }
        LogBox.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        _logLength = 0;
    }

    // ================= 窗口生命周期 =================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _engine.Devices.ControllerArrived += _ => Dispatcher.BeginInvoke(() => { RefreshDeviceCards(); RefreshLiveDevices(); });
        _engine.Devices.ControllerRemoved += _ => Dispatcher.BeginInvoke(() => { RefreshDeviceCards(); RefreshLiveDevices(); });
        // 自动切配置（按游戏）后 UI 跟进刷新
        _engine.ActiveProfileChanged += () => Dispatcher.BeginInvoke(() =>
        {
            _loading = true;
            RefreshProfileBox();
            LoadProfileToControls();
            _loading = false;
        });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ForceClose)
        {
            e.Cancel = true;
            Hide();
            if (!_balloonShown)
            {
                _balloonShown = true;
                HideToTrayRequested?.Invoke();
            }
            return;
        }
        _engine.Log -= AppendLog;
        _deviceTimer.Stop();
        _liveTimer.Stop();
        _captureTimer.Stop();
        StopRumbleTestTimer();
        base.OnClosing(e);
    }
}

/// <summary>设备页卡片视图模型。</summary>
public sealed class DeviceCard : System.ComponentModel.INotifyPropertyChanged
{
    private readonly ProControllerSession _session;
    private string _batteryText = "";
    private double _batteryPercent;

    public DeviceCard(ProControllerSession session) => _session = session;

    public string Title => $"{KindName(_session.Kind)} · 玩家 {_session.PlayerIndex + 1}";

    private static string KindName(ControllerKind kind) => kind switch
    {
        ControllerKind.JoyConLeft => "Joy-Con L",
        ControllerKind.JoyConRight => "Joy-Con R",
        _ => "Switch Pro Controller",
    };

    public string Detail =>
        $"{(_session.IsBluetooth ? "蓝牙" : "USB")}    " +
        $"校准：{(_session.CalibrationLoaded ? "SPI 已读取" : "默认值")}    " +
        $"报告 {_session.ReportHz:F0}Hz    处理 {_session.AvgProcessMs:F2}ms";

    public double BatteryPercent
    {
        get => _batteryPercent;
        set { _batteryPercent = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BatteryPercent))); }
    }

    public string BatteryText
    {
        get => _batteryText;
        set { _batteryText = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BatteryText))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Detail)));
        var st = _session.LatestState;
        if (st is null)
        {
            BatteryText = "等待输入数据…";
            BatteryPercent = 0;
            return;
        }
        var s = st.Value;
        BatteryPercent = Math.Clamp(s.BatteryLevel / 8.0 * 100, 0, 100);
        BatteryText = $"电量 {s.BatteryLevel}/8{(s.Charging ? "（充电中）" : "")}";
    }
}

/// <summary>输入设备下拉项。</summary>
public sealed class SessionItem
{
    public ProControllerSession Session { get; }
    public string Text { get; }

    public SessionItem(ProControllerSession session)
    {
        Session = session;
        Text = $"Pro Controller {session.PlayerIndex + 1} · {(session.IsBluetooth ? "蓝牙" : "USB")}";
    }
}

/// <summary>开机自启（HKCU Run）。</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SwitchPro";

    public static bool IsSet()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法获取可执行文件路径");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>极简输入对话框（新建配置命名用）。</summary>
public static class Prompt
{
    public static string? ShowDialog(Window owner, string title, string defaultValue)
    {
        string? result = null;
        var win = new Window
        {
            Title = title,
            Width = 360,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x33)),
        };
        var box = new TextBox { Text = defaultValue, Margin = new Thickness(12), VerticalContentAlignment = VerticalAlignment.Center };
        var ok = new Button { Content = "确定", Width = 72, Margin = new Thickness(4), IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 72, Margin = new Thickness(4), IsCancel = true };
        ok.Click += (_, _) => { result = box.Text.Trim(); win.Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        win.Content = panel;
        win.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
