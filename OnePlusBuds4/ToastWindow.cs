using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Application = System.Windows.Application;

namespace OnePlusBuds4;

/// <summary>
/// Small always-on-top popup (connect card / noise-mode change). Never steals focus,
/// slides up from the bottom-right corner, fades out by itself.
/// </summary>
public sealed class ToastWindow : Window
{
    static ToastWindow? _current;
    readonly DispatcherTimer _life;

    const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    ToastWindow(string icon, string title, string? subtitle, double? ring, double seconds)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 20, 12) };
        var ic = new TextBlock { Text = icon, FontSize = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Foreground = Brushes.White };
        row.Children.Add(ic);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        if (!string.IsNullOrEmpty(subtitle))
            col.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC4)), Margin = new Thickness(0, 2, 0, 0) });
        if (ring is double pct)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = pct, Height = 4, Width = 200, Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64)), Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), BorderThickness = new Thickness(0) };
            col.Children.Add(bar);
        }
        row.Children.Add(col);

        Content = new Border
        {
            Margin = new Thickness(14),
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1D, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5 },
            Child = row
        };

        Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 8;
            double finalTop = wa.Bottom - ActualHeight - 8;
            BeginAnimation(TopProperty, new DoubleAnimation(finalTop + 24, finalTop, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        };
        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _life.Tick += (_, _) => Dismiss();
    }

    void Dismiss()
    {
        _life.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Show (replacing any popup already on screen). Safe to call from any thread.</summary>
    public static void Show(string icon, string title, string? subtitle = null, double? percent = null, double seconds = 4)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var old = _current; _current = null;
                if (old != null) { old._life.Stop(); old.Close(); }
                var t = new ToastWindow(icon, title, subtitle, percent, seconds);
                _current = t;
                t.Closed += (_, _) => { if (_current == t) _current = null; };
                t.Show();
                t._life.Start();
            }
            catch { }
        });
    }
}
