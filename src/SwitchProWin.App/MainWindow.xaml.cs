using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchProWin.App.Controls;
using SwitchProWin.Engine;
using SwitchProWin.Mapper;
using SwitchProWin.Protocol;

namespace SwitchProWin.App;

public partial class MainWindow : Window
{
    private readonly SwitchProWinEngine _engine;
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
    private static string[] CurveNames() =>
        [Loc.S("curve.linear"), Loc.S("curve.gentle"), Loc.S("curve.exp"), Loc.S("curve.aggrExp"), Loc.S("curve.cubic")];

    private readonly List<SessionItem> _liveItems = new();
    private bool _loading = true;
    private bool _balloonShown;
    private int _logLength;

    public event Action? HideToTrayRequested;
    public bool ForceClose { get; set; }

    public MainWindow(SwitchProWinEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        Autostart.MigrateLegacyValue(); // 旧版本自启动值迁移（改名后的一次性清理）

        BuildSlots();
        foreach (var name in CurveNames())
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
        AppendLog(Loc.S("log.panelOpened"));

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
            var turboItem = new MenuItem { Header = Loc.S("slot.turbo") };
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
        Add(XboxButtons.LeftThumb, Loc.S("slot.ls"), PanelLS);
        Add(XboxButtons.RightThumb, Loc.S("slot.rs"), PanelRS);
        Add(XboxButtons.DpadLeft, Loc.S("dpad.left"), PanelDLeft);
        Add(XboxButtons.DpadUp, Loc.S("dpad.up"), PanelDUp);
        Add(XboxButtons.DpadDown, Loc.S("dpad.down"), PanelDDown);
        Add(XboxButtons.DpadRight, Loc.S("dpad.right"), PanelDRight);
    }

    private static string ProName(ProButtons b) => b switch
    {
        ProButtons.A => "A", ProButtons.B => "B", ProButtons.X => "X", ProButtons.Y => "Y",
        ProButtons.L => "L", ProButtons.R => "R", ProButtons.Zl => "ZL", ProButtons.Zr => "ZR",
        ProButtons.Minus => "-", ProButtons.Plus => "+",
        ProButtons.LStickPress => Loc.S("pro.lstick"), ProButtons.RStickPress => Loc.S("pro.rstick"),
        ProButtons.Home => "HOME", ProButtons.Capture => "CAPTURE",
        ProButtons.Up => Loc.S("pro.dpadUp"), ProButtons.Down => Loc.S("pro.dpadDown"),
        ProButtons.Left => Loc.S("pro.dpadLeft"), ProButtons.Right => Loc.S("pro.dpadRight"),
        _ => b.ToString(),
    };

    /// <summary>槽位右键：切换该槽位绑定键的连发。</summary>
    private void ToggleTurbo(XboxButtons target)
    {
        var profile = _engine.ActiveProfile;
        var bound = profile.BindingOf(target);
        if (bound is not { } pro)
        {
            AppendLog(Loc.F("log.turbo.unbound", target));
            return;
        }
        var name = pro.ToString();
        if (profile.TurboButtons.Contains(name))
        {
            profile.TurboButtons.Remove(name);
            AppendLog(Loc.F("log.turbo.off", ProName(pro)));
        }
        else
        {
            profile.TurboButtons.Add(name);
            AppendLog(Loc.F("log.turbo.on", ProName(pro)));
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
                ? Loc.F("slot.bound", ProName(pb)) + (turbo ? Loc.S("slot.turboTag") : "")
                : Loc.S("slot.unmapped");
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
            MessageBox.Show(Loc.S("msg.connectFirstMap"), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _captureSlot = button;
        _captureTarget = target;
        _captureInitial = session.LatestState?.Buttons ?? ProButtons.None;
        _captureStart = DateTime.UtcNow;
        _captureSessionId = session.PlayerIndex;
        button.Content = Loc.S("slot.waiting");
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
        AppendLog(Loc.F("log.mapped", _captureTarget, ProName(captured)));

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

    // 体感激活键选项：(显示名, 配置值 null=始终)。显示名随语言，每次调用现取
    private static (string Display, string? Value)[] GyroActivateOptions() =>
    [
        (Loc.S("gyroAct.always"), null),
        (Loc.F("gyroAct.hold", "ZL"), nameof(ProButtons.Zl)),
        (Loc.F("gyroAct.hold", "ZR"), nameof(ProButtons.Zr)),
        (Loc.F("gyroAct.hold", "L"), nameof(ProButtons.L)),
        (Loc.F("gyroAct.hold", "R"), nameof(ProButtons.R)),
        (Loc.F("gyroAct.hold", Loc.S("pro.lstick")), nameof(ProButtons.LStickPress)),
        (Loc.F("gyroAct.hold", Loc.S("pro.rstick")), nameof(ProButtons.RStickPress)),
        (Loc.F("gyroAct.hold", "HOME"), nameof(ProButtons.Home)),
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
            foreach (var (display, _) in GyroActivateOptions())
                GyroActivateBox.Items.Add(display);
        GyroActivateBox.SelectedIndex = Math.Max(0,
            Array.FindIndex(GyroActivateOptions(), o => o.Value == p.GyroActivationButton));

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
        // 语言选择（_loading 为 true 时不会触发保存）
        LanguageBox.SelectedIndex = _engine.Config.Language switch { "zh" => 1, "en" => 2, _ => 0 };

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
        AppendLog(Loc.F("log.autoswitch", _engine.ActiveProfile.Name,
            list.Count > 0 ? string.Join(", ", list) : Loc.S("word.none")));
    }

    // ================= 配置导入/导出 =================

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.S("dlg.exportTitle"),
            Filter = Loc.S("dlg.jsonFilter"),
            FileName = $"{_engine.ActiveProfile.Name}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_engine.ActiveProfile,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            AppendLog(Loc.F("log.exported", dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("msg.exportFailed", ex.Message), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.S("dlg.importTitle"),
            Filter = Loc.S("dlg.jsonFilter"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            var imported = System.Text.Json.JsonSerializer.Deserialize<ControllerProfile>(json);
            if (imported is null)
            {
                MessageBox.Show(Loc.S("msg.invalidProfile"), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            AppendLog(Loc.F("log.imported", name));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("msg.importFailed", ex.Message), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        AppendLog(Loc.F("log.profileSaved", _engine.ActiveProfile.Name));
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt.ShowDialog(this, Loc.S("dlg.newProfileTitle"),
            string.Format(Loc.S("dlg.newProfileDefault"), DateTime.Now));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_engine.Config.Profiles.Any(p => p.Name == name))
        {
            MessageBox.Show(Loc.S("msg.duplicateName"), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (MessageBox.Show(Loc.F("msg.deleteConfirm", name), "SwitchProWin", MessageBoxButton.YesNo,
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
            ? GyroActivateOptions()[Math.Clamp(GyroActivateBox.SelectedIndex, 0, GyroActivateOptions().Length - 1)].Value
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
        catch (Exception ex) { AppendLog(Loc.F("log.saveFailed", ex.Message)); }
    }

    private void GyroCalibrate_Click(object sender, RoutedEventArgs e)
    {
        var session = SelectedSession;
        if (session is null)
        {
            MessageBox.Show(Loc.S("msg.connectFirst"), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        session.CalibrateGyroNow();
        AppendLog(Loc.S("log.gyroRecenter"));
    }

    // ================= 摇杆校准向导 =================

    private void CalibrateLeft_Click(object sender, RoutedEventArgs e) => OpenCalibrationWizard(left: true);
    private void CalibrateRight_Click(object sender, RoutedEventArgs e) => OpenCalibrationWizard(left: false);

    private void OpenCalibrationWizard(bool left)
    {
        var session = SelectedSession;
        if (session is null)
        {
            MessageBox.Show(Loc.S("msg.connectFirstCal"), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new CalibrationWizard(session, left) { Owner = this }.ShowDialog();
        AppendLog(Loc.F("log.calDone", Loc.S(left ? "dir.left" : "dir.right")));
    }

    private void UpdateMappingLabels()
    {
        LeftDeadzoneLabel.Text = Loc.F("lbl.deadzone", LeftDeadzoneSlider.Value);
        RightDeadzoneLabel.Text = Loc.F("lbl.deadzone", RightDeadzoneSlider.Value);
        RumbleLabel.Text = $"{RumbleSlider.Value:0}%";
        GyroSensSlider.ToolTip = Loc.F("tip.sens", GyroSensSlider.Value);
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
            MessageBox.Show(Loc.S("msg.connectFirstRumble"), "SwitchProWin",
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
                    AppendLog(Loc.F("log.rumbleLow", RumbleSlider.Value));
                    break;
                case "high":
                    session.SetRumble(0, 255);
                    AppendLog(Loc.F("log.rumbleHigh", RumbleSlider.Value));
                    break;
                case "both":
                    session.SetRumble(255, 255);
                    AppendLog(Loc.F("log.rumbleBoth", RumbleSlider.Value));
                    break;
                case "stop":
                    session.SetRumble(0, 0);
                    return; // 手动停止，无需定时器
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("msg.rumbleFailed", ex.Message), "SwitchProWin",
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
            MessageBox.Show(Loc.F("msg.autostartFailed", ex.Message), "SwitchProWin", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    // ================= 语言 =================

    /// <summary>切换语言：保存配置并询问是否立即重启（重启后新语言全面生效）。</summary>
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var lang = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        if (lang == _engine.Config.Language) return;
        _engine.Config.Language = lang;
        try { _engine.Config.Save(); }
        catch (Exception ex) { AppendLog(Loc.F("log.saveFailed", ex.Message)); return; }
        if (MessageBox.Show(Loc.S("msg.langRestart"), "SwitchProWin",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            ((App)Application.Current).RestartApplication();
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

    public string Title => Loc.F("device.title", KindName(_session.Kind), _session.PlayerIndex + 1);

    private static string KindName(ControllerKind kind) => kind switch
    {
        ControllerKind.JoyConLeft => "Joy-Con L",
        ControllerKind.JoyConRight => "Joy-Con R",
        _ => "Switch Pro Controller",
    };

    public string Detail => Loc.F("device.detail",
        _session.IsBluetooth ? Loc.S("word.bt") : Loc.S("word.usb"),
        _session.CalibrationLoaded ? Loc.S("cal.spi") : Loc.S("cal.defaults"),
        _session.ReportHz, _session.AvgProcessMs);

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
            BatteryText = Loc.S("device.waiting");
            BatteryPercent = 0;
            return;
        }
        var s = st.Value;
        BatteryPercent = Math.Clamp(s.BatteryLevel / 8.0 * 100, 0, 100);
        BatteryText = Loc.F("device.battery", s.BatteryLevel, s.Charging ? Loc.S("device.charging") : "");
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
        Text = Loc.F("session.item", session.PlayerIndex + 1,
            session.IsBluetooth ? Loc.S("word.bt") : Loc.S("word.usb"));
    }
}

/// <summary>开机自启（HKCU Run）。</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SwitchProWin";
    private const string LegacyValueName = "SwitchPro"; // 改名前的旧值，启动时迁移并清理

    public static bool IsSet()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>旧版自启动值迁移：旧值 "SwitchPro" 存在则按其意图写入新值（指向当前 exe）并删除旧值。</summary>
    public static void MigrateLegacyValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(LegacyValueName) is null) return;
        if (key.GetValue(ValueName) is null && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\"");
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
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
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false); // 顺带清残留旧值
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
