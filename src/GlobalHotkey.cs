using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DshBar;

/// <summary>
/// Win32 RegisterHotKey 的极简封装：把全局热键挂到指定窗口的消息循环上。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Window _window;
    private readonly uint _modifiers;
    private readonly uint _vk;
    private readonly int _id;
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkey(Window window, uint modifiers, Key key)
    {
        _window = window;
        _modifiers = modifiers | MOD_NOREPEAT;
        _vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        _id = GetHashCode() & 0x7FFF;
    }

    public void Register()
    {
        var helper = new WindowInteropHelper(_window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle)
                  ?? throw new InvalidOperationException("无法获取窗口句柄");
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(helper.Handle, _id, _modifiers, _vk);
        if (!_registered)
            throw new InvalidOperationException(
                $"全局热键注册失败（错误 {Marshal.GetLastWin32Error()}），可能被其他程序占用。");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(new WindowInteropHelper(_window).Handle, _id);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
    }
}
