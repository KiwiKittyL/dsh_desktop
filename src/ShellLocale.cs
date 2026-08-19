using System;
using System.Collections.Generic;
using System.IO;

namespace DshBar;

/// <summary>
/// 跟随 harness Web GUI 的语言：读取并监听 $DSH_HOME/settings.yaml
/// （默认 ~/.dsh/settings.yaml）里的 locale.preference（zh/en）。
/// 文件变化时重读，值变化才触发 Changed（在 watcher 线程上，UI 需自行切线程）。
/// </summary>
public static class ShellLocale
{
    private static readonly Dictionary<string, (string Zh, string En)> Texts = new()
    {
        ["menu.toggle"]              = ("显示 / 隐藏", "Show / Hide"),
        ["menu.restart"]             = ("重启服务", "Restart Service"),
        ["menu.update"]              = ("检查更新", "Check for Updates"),
        ["menu.autostart"]           = ("开机自启动", "Launch at Login"),
        ["menu.quit"]                = ("退出", "Exit"),
        ["menu.main"]                = ("菜单", "Menu"),
        ["status.unknown"]           = ("尚未连接", "Not connected"),
        ["status.starting"]          = ("正在连接 / 启动 dsh 服务…", "Connecting / starting dsh service…"),
        ["status.online"]            = ("dsh 服务在线", "dsh service online"),
        ["status.failed"]            = ("连接失败", "Connection failed"),
        ["loading.prepare"]          = ("正在准备…", "Preparing…"),
        ["loading.loading"]          = ("正在加载 {0} …", "Loading {0} …"),
        ["loading.failed"]           = ("启动失败。", "Startup failed."),
        ["loading.pagefailed"]       = ("页面加载失败。", "Failed to load the page."),
        ["loading.retry"]            = ("重试", "Retry"),
        ["loading.restarting"]       = ("正在重启 dsh 服务…", "Restarting dsh service…"),
        ["loading.restartfailed"]    = ("重启失败。", "Restart failed."),
        ["loading.updating"]         = ("正在更新 dsh …", "Updating dsh …"),
        ["loading.updatefailed"]     = ("更新失败。", "Update failed."),
        ["balloon.updatefailed"]     = ("检查更新失败", "Update Check Failed"),
        ["balloon.updatetitle"]      = ("检查更新", "Check for Updates"),
        ["balloon.uptodate"]         = ("已是最新版本（{0}）。", "You're on the latest version ({0})."),
        ["balloon.updatedone.title"] = ("更新完成", "Update Complete"),
        ["balloon.updatedone"]       = ("已更新到 {0} 并重启服务。", "Updated to {0} and restarted the service."),
        ["balloon.autostartfailed"]  = ("开机自启动设置失败", "Failed to Set Auto-Start"),
        ["balloon.servicefailed"]    = ("dsh 服务启动失败", "Failed to Start dsh Service"),
        ["dialog.newversion"]        = ("发现新版本 {0}（当前 {1}）。\n\n是否现在更新并重启服务？",
                                        "Version {0} is available (current: {1}).\n\nUpdate now and restart the service?"),
    };

    /// <summary>当前语言："zh" 或 "en"。缺省/无法读取时为 zh。</summary>
    public static string Current { get; private set; } = "zh";

    /// <summary>语言切换事件。触发线程不定，UI 订阅后需 Dispatcher 切换。</summary>
    public static event Action? Changed;

    private static FileSystemWatcher? _watcher;

    public static string T(string key) =>
        Texts.TryGetValue(key, out var pair)
            ? (Current == "en" ? pair.En : pair.Zh)
            : key;

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    public static void Initialize()
    {
        Apply(ReadPreference());
        WatchSettings();
    }

    private static string SettingsPath()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return Path.Combine(home, "settings.yaml");
    }

    /// <summary>行扫描 settings.yaml：顶层 locale: 段内的 preference 值。</summary>
    private static string ReadPreference()
    {
        try
        {
            var inLocale = false;
            foreach (var line in File.ReadLines(SettingsPath()))
            {
                if (line.Length == 0) continue;

                if (line[0] is ' ' or '\t')
                {
                    if (inLocale)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("preference:", StringComparison.Ordinal))
                        {
                            var value = trimmed["preference:".Length..].Trim().Trim('"', '\'');
                            return value == "en" ? "en" : "zh";
                        }
                    }
                    continue;
                }

                inLocale = line.Trim() == "locale:";
            }
        }
        catch (IOException) { /* 文件不存在或写入中途，按默认 zh */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
        return "zh";
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
        _watcher.Changed += (_, _) => Apply(ReadPreference());
        _watcher.Created += (_, _) => Apply(ReadPreference());
        _watcher.Renamed += (_, _) => Apply(ReadPreference());
    }

    private static void Apply(string preference)
    {
        if (preference == Current) return;
        Current = preference;
        Changed?.Invoke();
    }
}
