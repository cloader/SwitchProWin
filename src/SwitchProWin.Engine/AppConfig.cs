using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchProWin.Mapper;
using SwitchProWin.Protocol;

namespace SwitchProWin.Engine;

/// <summary>
/// 应用配置（%APPDATA%\SwitchProWin\config.json）。
/// 含多份 <see cref="ControllerProfile"/>；旧版单配置字段保留用于迁移。
/// </summary>
public sealed class AppConfig
{
    // ---- 多配置文件 ----

    public List<ControllerProfile> Profiles { get; set; } = new();
    public string ActiveProfileName { get; set; } = "默认";

    // ---- Joy-Con ----

    /// <summary>Joy-Con 使用模式（配对合并 / 单只独立）。</summary>
    public JoyConMode JoyConMode { get; set; } = JoyConMode.Pair;

    // ---- 组合键宏 ----

    /// <summary>L+R+Minus 切换到下一个配置文件。</summary>
    public bool ComboProfileCycle { get; set; } = true;

    /// <summary>L+R+Plus 陀螺仪重定心（重新校零）。</summary>
    public bool ComboGyroRecenter { get; set; } = true;

    // ---- 界面语言 ----

    /// <summary>界面语言："auto"（按系统 UI 语言）/ "zh" / "en"。切换后重启生效。</summary>
    public string Language { get; set; } = "auto";

    // ---- 旧版单配置字段（v1 遗留，Load 时迁移进 Profiles 后不再使用） ----

    public bool NintendoLayout { get; set; } = true;
    public double LeftDeadzone { get; set; } = 0.10;
    public double RightDeadzone { get; set; } = 0.10;
    public double LeftOuterDeadzone { get; set; } = 0.04;
    public double RightOuterDeadzone { get; set; } = 0.04;
    public StickCurve LeftCurve { get; set; } = StickCurve.GentleQuad;
    public StickCurve RightCurve { get; set; } = StickCurve.GentleQuad;
    public float RumbleMaxAmplitude { get; set; } = RumbleEncoder.DefaultMaxAmplitude;
    public bool Autostart { get; set; }

    internal static string? ConfigDirOverride; // 单元测试重定向

    private static string ConfigDir => ConfigDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchProWin");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>旧版数据目录一次性迁移：SwitchPro → SwitchProWin（config.json / log.txt 随目录搬移）。
    /// 尽力而为：迁移失败不阻塞启动（下次启动再试）。单元测试重定向时不触碰真实 APPDATA。</summary>
    private static void MigrateLegacyDir()
    {
        if (ConfigDirOverride is not null) return;
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchPro");
            if (!Directory.Exists(legacyDir)) return;
            if (!Directory.Exists(ConfigDir))
            {
                Directory.Move(legacyDir, ConfigDir);
                return;
            }
            // 两目录并存（如新目录已被日志先建出）：仅补迁缺失文件
            foreach (var name in new[] { "config.json", "log.txt" })
            {
                var src = Path.Combine(legacyDir, name);
                var dst = Path.Combine(ConfigDir, name);
                if (File.Exists(src) && !File.Exists(dst))
                    File.Move(src, dst);
            }
        }
        catch
        {
            // 旧目录被占用（如旧版本仍在运行）等：忽略，待下次启动迁移
        }
    }

    /// <summary>当前生效的配置文件（保证非 null；缺失时回退第一份或新建默认）。</summary>
    public ControllerProfile GetActive()
    {
        EnsureProfiles();
        return Profiles.FirstOrDefault(p => p.Name == ActiveProfileName)
               ?? Profiles[0];
    }

    /// <summary>确保至少有一份配置；旧版字段存在时迁移为默认配置（名称随界面语言）。</summary>
    public void EnsureProfiles()
    {
        if (Profiles.Count == 0)
        {
            var legacy = new ControllerProfile
            {
                Name = Loc.S("profile.default"),
                ButtonMap = NintendoLayout
                    ? DefaultButtonMap.CreateNintendoLayout()
                    : DefaultButtonMap.CreateXboxLayout(),
                LeftDeadzone = LeftDeadzone,
                RightDeadzone = RightDeadzone,
                LeftOuterDeadzone = LeftOuterDeadzone,
                RightOuterDeadzone = RightOuterDeadzone,
                LeftCurve = LeftCurve,
                RightCurve = RightCurve,
                RumbleMaxAmplitude = RumbleMaxAmplitude,
            };
            Profiles.Add(legacy);
        }
    }

    public static AppConfig Load()
    {
        MigrateLegacyDir();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                Loc.Apply(config.Language); // 语言先于一切文本生成（含 EnsureProfiles 的默认配置名）
                config.EnsureProfiles();
                NormalizeActiveProfile(config);
                return config;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        var def = new AppConfig();
        Loc.Apply(def.Language);
        def.EnsureProfiles();
        return def;
    }

    /// <summary>ActiveProfileName 指向不存在的配置时归一化为第一份（改名换语言后默认名对不上时兜底）。</summary>
    private static void NormalizeActiveProfile(AppConfig config)
    {
        if (config.Profiles.Count > 0 && config.Profiles.All(p => p.Name != config.ActiveProfileName))
            config.ActiveProfileName = config.Profiles[0].Name;
    }

    private readonly object _saveLock = new();

    /// <summary>序列化并落盘（原子写：临时文件 + Replace，避免崩溃/断电中途损坏配置）。
    /// UI 线程与组合键任务线程可能并发保存，加锁串行化。</summary>
    public void Save()
    {
        EnsureProfiles();
        lock (_saveLock)
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(ConfigPath))
                File.Replace(tmp, ConfigPath, destinationBackupFileName: null);
            else
                File.Move(tmp, ConfigPath);
        }
    }

    /// <summary>把映射相关配置应用到一份映射模板（旧版单配置语义，保留给测试/CLI）。</summary>
    public void ApplyTo(MappingProfile profile)
    {
        profile.NintendoLayout = NintendoLayout;
        profile.LeftStick.InnerDeadzone = LeftDeadzone;
        profile.LeftStick.OuterDeadzone = LeftOuterDeadzone;
        profile.LeftStick.Curve = LeftCurve;
        profile.RightStick.InnerDeadzone = RightDeadzone;
        profile.RightStick.OuterDeadzone = RightOuterDeadzone;
        profile.RightStick.Curve = RightCurve;
    }
}
