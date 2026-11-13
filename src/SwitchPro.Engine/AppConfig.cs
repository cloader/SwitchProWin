using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchPro.Mapper;
using SwitchPro.Protocol;

namespace SwitchPro.Engine;

/// <summary>
/// 应用配置（%APPDATA%\SwitchPro\config.json）。
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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchPro");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>当前生效的配置文件（保证非 null；缺失时回退第一份或新建默认）。</summary>
    public ControllerProfile GetActive()
    {
        EnsureProfiles();
        return Profiles.FirstOrDefault(p => p.Name == ActiveProfileName)
               ?? Profiles[0];
    }

    /// <summary>确保至少有一份配置；旧版字段存在时迁移为"默认"配置。</summary>
    public void EnsureProfiles()
    {
        if (Profiles.Count == 0)
        {
            var legacy = new ControllerProfile
            {
                Name = "默认",
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
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                config.EnsureProfiles();
                return config;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        var def = new AppConfig();
        def.EnsureProfiles();
        return def;
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
