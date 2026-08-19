using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DshBar;

/// <summary>
/// 自绘轻量 Toast：右下角浮出的小卡片，替代 legacy BalloonTip
/// （Win10/11 上无 AppUserModelID 的程序气球会被系统静默丢弃）。
/// 跟随应用主题画刷，鲸鱼图标，淡入 4 秒后淡出，点击立即关闭，多条向上堆叠。
/// </summary>
public static class Toast
{
    private const double ToastWidth = 340;
    private const double Margin = 4;
    private const double Gap = 6;
    private static readonly TimeSpan StayTime = TimeSpan.FromSeconds(4);

    private static readonly List<Window> _open = new();

    public static void Show(string title, string message)
    {
        var window = BuildWindow(title, message);

        // 先测量真实高度再定位：按估算高度摆放会把差值留成底部空隙
        var card = (FrameworkElement)window.Content;
        card.Measure(new Size(ToastWidth, double.PositiveInfinity));
        var height = card.DesiredSize.Height;

        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Right - ToastWidth - Margin;
        window.Top = workArea.Bottom - height - Margin - StackedOffset();

        _open.Add(window);
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Reflow(workArea);
        };
        window.MouseLeftButtonDown += (_, _) => window.Close();

        window.Show();
        AnimateLifecycle(window);
    }

    private static Window BuildWindow(string title, string message)
    {
        var icon = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/whale.ico")),
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var textPanel = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        textPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
        });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textPanel, 1);
        body.Children.Add(icon);
        body.Children.Add(textPanel);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Background = (Brush)Application.Current.Resources["MenuBgBrush"],
            BorderBrush = (Brush)Application.Current.Resources["MenuBorderBrush"],
            BorderThickness = new Thickness(1),
            Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };

        return new Window
        {
            Width = ToastWidth,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = card,
            Opacity = 0,
        };
    }

    private static void AnimateLifecycle(Window window)
    {
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(150));
        window.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = StayTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => window.Close();
            window.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        timer.Start();
    }

    // 堆叠偏移：已开 Toast 的真实高度之和（它们都已 Show，ActualHeight 有效）
    private static double StackedOffset() => _open.Sum(w => w.ActualHeight + Gap);

    private static void Reflow(Rect workArea)
    {
        var offset = 0.0;
        foreach (var w in _open)
        {
            w.Top = workArea.Bottom - w.ActualHeight - Margin - offset;
            offset += w.ActualHeight + Gap;
        }
    }
}
