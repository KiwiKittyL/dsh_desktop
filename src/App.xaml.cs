using System;
using System.Threading;
using System.Windows;

namespace DshBar;

public partial class App : Application
{
    /// <summary>双击间隔上限：两次按 H 相隔小于该值才触发显隐。</summary>
    private static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(500);

    private static Mutex? _singleInstance;
    private MainWindow? _window;
    private GlobalHotkey? _hotkey;
    private DateTime _lastPress = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：已有一个 DshBar 在跑就直接退出（那个实例响应热键）
        _singleInstance = new Mutex(initiallyOwned: true, "DshBar.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _window = new MainWindow();

        // 热键：按住 Alt 连续按两下 H —— 唤出 / 隐藏
        // RegisterHotKey 只能表达组合键，"双击"靠 500ms 内触发两次来识别；
        // GlobalHotkey 内部带 MOD_NOREPEAT，按住 H 不松手的键盘自动重复不算数。
        _hotkey = new GlobalHotkey(_window,
            GlobalHotkey.MOD_ALT, System.Windows.Input.Key.H);
        _hotkey.Pressed += OnHotkeyPressed;
        _hotkey.Register();

        // 开机自启动（--background）时静默驻留：不弹窗，托盘和热键照常工作
        var background = e.Args.Contains(AutoStartManager.BackgroundArgument,
            StringComparer.OrdinalIgnoreCase);
        if (!background)
            _window.ShowAndFocus();
    }

    private void OnHotkeyPressed()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPress < DoublePressWindow)
        {
            _lastPress = DateTime.MinValue;
            _window?.ToggleVisibility();
        }
        else
        {
            _lastPress = now;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
