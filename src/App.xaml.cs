using System;
using System.Threading;
using System.Windows;

namespace DshBar;

public partial class App : Application
{
    /// <summary>双击间隔上限：两次按 H 相隔小于该值才触发显隐。</summary>
    private static readonly TimeSpan DoublePressWindow = TimeSpan.FromMilliseconds(500);

    private static Mutex? _singleInstance;
    private static bool _ownsMutex;
    private MainWindow? _window;
    private GlobalHotkey? _hotkey;
    private DateTime _lastPress = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：已有一个 DshBar 在跑就直接退出（那个实例响应热键）
        _singleInstance = new Mutex(initiallyOwned: true, "DshBar.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 注意：Mutex 已存在时 initiallyOwned 被忽略，本进程并不持有所有权，
            // 绝不能 ReleaseMutex（会抛 ApplicationException）。直接释放句柄走人。
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        _ownsMutex = true;

        base.OnStartup(e);
        _window = new MainWindow();

        // 热键：按住 Alt 连续按两下 H —— 唤出 / 隐藏
        // RegisterHotKey 只能表达组合键，"双击"靠 500ms 内触发两次来识别；
        // GlobalHotkey 内部带 MOD_NOREPEAT，按住 H 不松手的键盘自动重复不算数。
        _hotkey = new GlobalHotkey(_window,
            GlobalHotkey.MOD_ALT, System.Windows.Input.Key.H);
        _hotkey.Pressed += OnHotkeyPressed;
        _hotkey.Register();

        // 开机自启动（--background）时静默驻留：不弹窗，托盘和热键照常工作；
        // 同时在后台嗅探/启动 dsh web，让服务随开机就绪，首次唤出秒开
        var background = e.Args.Contains(AutoStartManager.BackgroundArgument,
            StringComparer.OrdinalIgnoreCase);
        if (background)
            _ = _window.WarmUpServiceAsync();
        else
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
        if (_ownsMutex)
        {
            try { _singleInstance?.ReleaseMutex(); }
            catch (ApplicationException) { /* 所有权已丢失时忽略 */ }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
