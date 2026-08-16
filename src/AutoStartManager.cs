using System;
using Microsoft.Win32;

namespace DshBar;

/// <summary>
/// 开机自启动管理：HKCU\...\Run 下写本程序路径 + --background 参数。
/// 参数是关键——带它启动时窗口保持隐藏，只驻留托盘和热键。
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DshBar";

    /// <summary>启动参数：由 Run 键带来，表示“开机自启，不要弹窗”。</summary>
    public const string BackgroundArgument = "--background";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开注册表 Run 键");

        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定本程序路径");
            key.SetValue(ValueName, $"\"{exe}\" {BackgroundArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
