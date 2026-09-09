using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace DshBar;

public partial class MainWindow : Window
{
    private readonly DshServiceGuardian _guardian = new();
    private readonly DshUpdateChecker _updateChecker = new();
    private CancellationTokenSource? _startupCts;
    private bool _webReady;
    private ServiceStatus _status = ServiceStatus.Unknown;

    public MainWindow()
    {
        InitializeComponent();
        AutoStartItem.IsChecked = WindowAutoStartItem.IsChecked = AutoStartManager.IsEnabled();
        SourceInitialized += (_, _) => EnableRoundedCorners();

        // 跟随 harness Web GUI 的语言设置
        ShellLocale.Initialize();
        ShellLocale.Changed += () => Dispatcher.Invoke(ApplyLocale);
        ApplyLocale();

        // 跟随 harness Web GUI 的主题设置（含 system → Windows 主题）
        ShellTheme.Initialize();
        ShellTheme.Changed += () => Dispatcher.Invoke(ShellTheme.ApplyToResources);
        ShellTheme.ApplyToResources();

        SetStatus(ServiceStatus.Unknown);
    }

    /// <summary>按当前语言重刷所有静态文本（菜单、提示、按钮）。</summary>
    private void ApplyLocale()
    {
        TrayToggleItem.Header = WinToggleItem.Header = ShellLocale.T("menu.toggle");
        TrayRestartItem.Header = WinRestartItem.Header = ShellLocale.T("menu.restart");
        TrayUpdateItem.Header = WinUpdateItem.Header = ShellLocale.T("menu.update");
        AutoStartItem.Header = WindowAutoStartItem.Header = ShellLocale.T("menu.autostart");
        TrayQuitItem.Header = WinQuitItem.Header = ShellLocale.T("menu.quit");

        WhaleArea.ToolTip = ShellLocale.T("menu.main");
        RetryButton.Content = ShellLocale.T("loading.retry");

        SetStatus(_status); // 状态点提示语跟随重刷
    }

    #region 气球提示（自绘 Toast；legacy BalloonTip 在 Win11 会被静默丢弃）

    private void Balloon(string title, string message) => Toast.Show(title, message);

    #endregion

    #region 状态点

    private enum ServiceStatus { Unknown, Starting, Online, Failed }

    private void SetStatus(ServiceStatus status)
    {
        _status = status;
        var (color, tip) = status switch
        {
            ServiceStatus.Starting => ("#FFFBBF24", ShellLocale.T("status.starting")),
            ServiceStatus.Online   => ("#FF4ADE80", ShellLocale.T("status.online")),
            ServiceStatus.Failed   => ("#FFF87171", ShellLocale.T("status.failed")),
            _                      => ("#FF5A5A60", ShellLocale.T("status.unknown")),
        };
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        StatusDot.ToolTip = tip;
    }

    #endregion

    #region Win11 DWM 圆角

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>请 DWM 把窗口四角裁成圆角（Win11 特性；旧系统调用失败也无害）。</summary>
    private void EnableRoundedCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    #endregion

    #region 边缘拖动调整尺寸（原生 NCHITTEST 路径）

    private const int WM_NCLBUTTONDOWN = 0x00A1;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    /// <summary>
    /// 热区按下即把后续鼠标轨迹交给系统的非客户区调整逻辑，
    /// 手感与普通窗口边框完全一致。Tag 里是 HT* 代码
    /// （HTLEFT=10, HTRIGHT=11, HTTOP=12, HTTOPLEFT=13, HTTOPRIGHT=14,
    /// HTBOTTOM=15, HTBOTTOMLEFT=16, HTBOTTOMRIGHT=17）。
    /// </summary>
    private void ResizeEdge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string tag)
            return;

        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle,
            WM_NCLBUTTONDOWN, (IntPtr)int.Parse(tag), IntPtr.Zero);
        e.Handled = true;
    }

    #endregion

    public void ToggleVisibility()
    {
        if (IsVisible) Hide();
        else ShowAndFocus();
    }

    public void ShowAndFocus()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (!_webReady && _startupCts is null)
            _ = StartAndNavigateAsync();
    }

    /// <summary>
    /// 开机自启动的静默预热：不弹窗、不加载 WebView，只在后台探测/启动 dsh web。
    /// 状态点照常更新；失败时用 Toast 提示（主窗口隐藏也能看到）。
    /// 之后用户首次唤出窗口时，服务已在跑，直接导航秒开。
    /// </summary>
    public async Task WarmUpServiceAsync()
    {
        if (_webReady || _startupCts is not null)
            return;

        _startupCts = new CancellationTokenSource();
        SetStatus(ServiceStatus.Starting);

        try
        {
            await _guardian.EnsureRunningAsync(new Progress<string>(_ => { }), _startupCts.Token);
            SetStatus(ServiceStatus.Online);
        }
        catch (OperationCanceledException)
        {
            // 退出时取消，无需提示
        }
        catch (Exception ex)
        {
            SetStatus(ServiceStatus.Failed);
            Balloon(ShellLocale.T("balloon.servicefailed"), ex.Message);
        }
        finally
        {
            _startupCts.Dispose();
            _startupCts = null;
        }
    }

    /// <summary>探测/启动 dsh web，就绪后让 WebView2 导航过去。</summary>
    private async Task StartAndNavigateAsync()
    {
        _startupCts = new CancellationTokenSource();
        ShowLoading(ShellLocale.T("loading.prepare"), null);
        SetStatus(ServiceStatus.Starting);

        var status = new Progress<string>(msg => StatusText.Text = msg);

        try
        {
            var info = await _guardian.EnsureRunningAsync(status, _startupCts.Token);
            await EnsureWebViewAsync();
            // 自己拉起的服务带启动令牌（新版认证墙）；已有服务靠持久化 Cookie
            var target = info.LaunchUrl ?? info.BaseUrl;
            StatusText.Text = ShellLocale.T("loading.loading", info.BaseUrl);
            WebView.CoreWebView2!.Navigate(target.ToString());
        }
        catch (OperationCanceledException)
        {
            // 用户退出导致，无需提示
        }
        catch (Exception ex)
        {
            ShowLoading(ShellLocale.T("loading.failed"), ex.Message);
            SetStatus(ServiceStatus.Failed);
        }
        finally
        {
            _startupCts.Dispose();
            _startupCts = null;
        }
    }

    private async Task EnsureWebViewAsync()
    {
        if (WebView.CoreWebView2 is not null)
            return;

        // 固定用户数据目录，避免每次全新 profile
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DshBar", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await WebView.EnsureCoreWebView2Async(env);

        WebView.CoreWebView2!.NavigationCompleted += (_, e) =>
        {
            // 新版 dsh web 的认证墙：401 说明没有有效 Cookie（服务不是我们拉起的，
            // 或 Cookie 已过期）。提示用户通过"重启服务"让 DshBar 接管启动以获取令牌。
            if (e.HttpStatusCode == 401)
            {
                _webReady = false;
                ShowLoading(ShellLocale.T("loading.authrequired"), null);
                SetStatus(ServiceStatus.Failed);
                return;
            }

            if (e.IsSuccess)
            {
                _webReady = true;
                LoadingPanel.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;
                SetStatus(ServiceStatus.Online);
            }
            else
            {
                ShowLoading(ShellLocale.T("loading.pagefailed"), $"WebView 错误码：{e.WebErrorStatus}");
                SetStatus(ServiceStatus.Failed);
            }
        };
    }

    private void ShowLoading(string message, string? error)
    {
        LoadingPanel.Visibility = Visibility.Visible;
        WebView.Visibility = Visibility.Collapsed;
        StatusText.Text = message;

        var hasError = error is not null;
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_startupCts is null)
            _ = StartAndNavigateAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    /// <summary>✕ 最小化到托盘（不退出程序）；退出走托盘/鲸鱼菜单。</summary>
    private void CloseToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void TrayToggle_Click(object sender, RoutedEventArgs e) => ToggleVisibility();

    private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => ShowAndFocus();

    /// <summary>重启 dsh 服务：杀旧起新，就绪后让 WebView 重新加载。</summary>
    private async void RestartService_Click(object sender, RoutedEventArgs e)
    {
        if (_startupCts is not null)
            return;

        _webReady = false;
        _startupCts = new CancellationTokenSource();
        ShowLoading(ShellLocale.T("loading.restarting"), null);
        SetStatus(ServiceStatus.Starting);

        var status = new Progress<string>(msg => StatusText.Text = msg);

        try
        {
            var info = await _guardian.RestartAsync(status, _startupCts.Token);
            await EnsureWebViewAsync();
            var target = info.LaunchUrl ?? info.BaseUrl;
            StatusText.Text = ShellLocale.T("loading.loading", info.BaseUrl);
            WebView.CoreWebView2!.Navigate(target.ToString());
        }
        catch (OperationCanceledException)
        {
            // 退出时取消，无需提示
        }
        catch (Exception ex)
        {
            ShowLoading(ShellLocale.T("loading.restartfailed"), ex.Message);
            SetStatus(ServiceStatus.Failed);
        }
        finally
        {
            _startupCts.Dispose();
            _startupCts = null;
        }
    }

    /// <summary>检查更新：无更新弹气泡；有更新询问后一键更新并重启服务。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_startupCts is not null)
            return;

        DshUpdateChecker.CheckResult result;
        try
        {
            result = await _updateChecker.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Balloon(ShellLocale.T("balloon.updatefailed"), ex.Message);
            return;
        }

        if (!result.UpdateAvailable)
        {
            Balloon(ShellLocale.T("balloon.updatetitle"),
                ShellLocale.T("balloon.uptodate", result.Local));
            return;
        }

        var choice = MessageBox.Show(
            ShellLocale.T("dialog.newversion", result.Latest, result.Local),
            ShellLocale.T("balloon.updatetitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
            return;

        await UpdateAndRestartAsync(result.Latest);
    }

    /// <summary>npm 全局更新 → 重启 dsh 服务 → WebView 重载。</summary>
    private async Task UpdateAndRestartAsync(string newVersion)
    {
        _webReady = false;
        _startupCts = new CancellationTokenSource();
        ShowLoading(ShellLocale.T("loading.updating"), null);
        SetStatus(ServiceStatus.Starting);

        var status = new Progress<string>(msg => StatusText.Text = msg);

        try
        {
            await _updateChecker.UpdateAsync(status, _startupCts.Token);
            var info = await _guardian.RestartAsync(status, _startupCts.Token);
            await EnsureWebViewAsync();
            var target = info.LaunchUrl ?? info.BaseUrl;
            StatusText.Text = ShellLocale.T("loading.loading", info.BaseUrl);
            WebView.CoreWebView2!.Navigate(target.ToString());
            Balloon(ShellLocale.T("balloon.updatedone.title"),
                ShellLocale.T("balloon.updatedone", newVersion));
        }
        catch (OperationCanceledException)
        {
            // 退出时取消，无需提示
        }
        catch (Exception ex)
        {
            ShowLoading(ShellLocale.T("loading.updatefailed"), ex.Message);
            SetStatus(ServiceStatus.Failed);
        }
        finally
        {
            _startupCts.Dispose();
            _startupCts = null;
        }
    }

    private void WhaleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 单击鲸鱼弹出菜单；Handled 阻止冒泡成标题栏拖动
        if (WhaleArea.ContextMenu is { } menu)
        {
            menu.PlacementTarget = WhaleArea;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    private void AutoStartItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item)
            return;

        try
        {
            AutoStartManager.SetEnabled(item.IsChecked);
        }
        catch (Exception ex)
        {
            Balloon(ShellLocale.T("balloon.autostartfailed"), ex.Message);
        }

        // 托盘菜单与鲸鱼菜单的两处勾选始终以注册表真实状态为准
        var enabled = AutoStartManager.IsEnabled();
        AutoStartItem.IsChecked = WindowAutoStartItem.IsChecked = enabled;
    }

    private void TrayQuit_Click(object sender, RoutedEventArgs e) => Quit();

    private void Quit()
    {
        _startupCts?.Cancel();
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }
}
