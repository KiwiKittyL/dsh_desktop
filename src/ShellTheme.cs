using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DshBar;

/// <summary>
/// 跟随 harness Web GUI 的主题：读取并监听 $DSH_HOME/settings.yaml 的
/// ui-theme.preference（light/dark/system）；system 时跟 Windows 应用主题
/// （注册表 AppsUseLightTheme，并监听系统主题切换）。
/// 配色取自 harness 设计令牌的 neutral-bluish 灰阶，两套主题共用一套资源键。
/// Changed 触发线程不定，UI 订阅后需 Dispatcher 切换；ApplyToResources 须在 UI 线程调用。
/// </summary>
public static class ShellTheme
{
    /// <summary>当前生效主题："dark" 或 "light"。</summary>
    public static string Current { get; private set; } = "dark";

    /// <summary>生效主题变化事件（preference 或系统主题变化导致）。</summary>
    public static event Action? Changed;

    private static string _preference = "system";
    private static FileSystemWatcher? _watcher;

    public static void Initialize()
    {
        _preference = ReadThemePreference();
        Apply(Evaluate());
        WatchSettings();
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (_preference == "system")
                Apply(Evaluate());
        };
    }

    private static string Evaluate()
    {
        if (_preference is "light" or "dark")
            return _preference;

        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1);
        return value is int useLight && useLight == 0 ? "dark" : "light";
    }

    /// <summary>行扫描 settings.yaml：顶层 ui-theme: 段内的 preference 值。</summary>
    private static string ReadThemePreference()
    {
        try
        {
            var inTheme = false;
            foreach (var line in File.ReadLines(SettingsPath()))
            {
                if (line.Length == 0) continue;

                if (line[0] is ' ' or '\t')
                {
                    if (inTheme)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("preference:", StringComparison.Ordinal))
                        {
                            var value = trimmed["preference:".Length..].Trim().Trim('"', '\'');
                            return value is "light" or "dark" ? value : "system";
                        }
                    }
                    continue;
                }

                inTheme = line.Trim() == "ui-theme:";
            }
        }
        catch (IOException) { /* 文件不存在或写入中途，按 system */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
        return "system";
    }

    private static string SettingsPath()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return Path.Combine(home, "settings.yaml");
    }

    private static void WatchSettings()
    {
        var path = SettingsPath();
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => OnSettingsChanged();
        _watcher.Created += (_, _) => OnSettingsChanged();
        _watcher.Renamed += (_, _) => OnSettingsChanged();
    }

    private static void OnSettingsChanged()
    {
        _preference = ReadThemePreference();
        Apply(Evaluate());
    }

    private static void Apply(string theme)
    {
        if (theme == Current) return;
        Current = theme;
        Changed?.Invoke();
    }

    /// <summary>把当前主题的全套画刷写进应用资源表（UI 线程调用）。</summary>
    public static void ApplyToResources()
    {
        var palette = Current == "light" ? Light : Dark;
        var resources = Application.Current.Resources;
        foreach (var (key, color) in palette)
            resources[key] = new SolidColorBrush(color);
    }

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>深色：sidebar-fill = neutral-bluish-900，其余按灰阶递推。</summary>
    private static readonly (string Key, Color Color)[] Dark =
    {
        ("ChromeBgBrush",       C(27, 27, 28)),     // 900
        ("TextPrimaryBrush",    C(207, 211, 214)),  // 300
        ("TextSecondaryBrush",  C(151, 157, 166)),  // 500
        ("BtnHoverBrush",       C(44, 44, 46)),     // 850
        ("BtnPressedBrush",     C(53, 54, 56)),     // 800
        ("MenuBgBrush",         C(35, 35, 36)),     // 875
        ("MenuBorderBrush",     C(53, 54, 56)),     // 800
        ("MenuItemHoverBrush",  C(44, 44, 46)),     // 850
        ("MenuDisabledFgBrush", C(90, 90, 96)),
        ("SeparatorBrush",      C(53, 54, 56)),     // 800
        ("ProgressTrackBrush",  C(44, 44, 46)),     // 850
    };

    /// <summary>浅色：sidebar-fill = neutral-bluish-50，其余按灰阶递推。</summary>
    private static readonly (string Key, Color Color)[] Light =
    {
        ("ChromeBgBrush",       C(249, 250, 251)),  // 50
        ("TextPrimaryBrush",    C(27, 27, 28)),     // 900
        ("TextSecondaryBrush",  C(129, 133, 140)),  // 600
        ("BtnHoverBrush",       C(235, 238, 242)),  // 100
        ("BtnPressedBrush",     C(225, 229, 238)),  // 200
        ("MenuBgBrush",         C(255, 255, 255)),  // 00
        ("MenuBorderBrush",     C(225, 229, 238)),  // 200
        ("MenuItemHoverBrush",  C(241, 243, 245)),  // 75
        ("MenuDisabledFgBrush", C(173, 178, 184)),  // 400
        ("SeparatorBrush",      C(235, 238, 242)),  // 100
        ("ProgressTrackBrush",  C(235, 238, 242)),  // 100
    };
}
