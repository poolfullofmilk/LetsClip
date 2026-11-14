using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LetsClip.Data.ViewModels;
using LetsClip.Desktop.Theming;
using MudBlazor.Utilities;

namespace LetsClip.Desktop.Interop;

public sealed partial class MonitorOverlay : Window
{
    // Click Through, Never Focused, Never On The Taskbar
    private const int ExtendedStyleIndex = -20;
    private const nint ClickThrough = 0x20;
    private const nint ToolWindow = 0x80;
    private const nint NeverActivates = 0x08000000;
    private const uint NoActivate = 0x0010;
    private static readonly nint s_topMost = -1;

    // Seen On The Monitor, Never In A Capture
    private const uint ExcludeFromCapture = 0x11;

    // The Edge Traces The Screen, The Name Sits Top Centre
    private const double EdgeThickness = 8;
    private const double NameSize = 22;

    // A Toast Takes Only A Band Along The Top
    private const int ToastBand = 200;
    private static readonly TimeSpan s_toastTime = TimeSpan.FromSeconds(2.5);
    private static readonly Duration s_fade = new(TimeSpan.FromMilliseconds(150));

    private readonly MonitorInfo _monitor;
    private readonly int _height;

    public MonitorOverlay(MonitorInfo monitor, bool isChosen)
        : this(monitor, monitor.Height)
    {
        Content = Frame(monitor, isChosen);
    }

    private MonitorOverlay(MonitorInfo monitor, int height)
    {
        _monitor = monitor;
        _height = height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        SourceInitialized += (_, _) => Place();
    }

    public static void Toast(MonitorInfo monitor, string text, string? detail, bool isWarning)
    {
        // The Name Badge's Look, Faded In Over A Game
        var palette = AppTheme.Custom.PaletteDark;
        var words = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = NameSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(isWarning ? palette.Warning : palette.TextPrimary),
        };
        words.Inlines.Add(new System.Windows.Documents.Run(text));
        if (detail is not null)
        {
            words.Inlines.Add(
                new System.Windows.Documents.Run($"  {detail}")
                {
                    FontWeight = FontWeights.Normal,
                    Foreground = BrushOf(palette.TextSecondary),
                }
            );
        }

        var toast = new MonitorOverlay(monitor, Math.Min(ToastBand, monitor.Height))
        {
            Content = Badge(words, palette.AppbarBackground),
            Opacity = 0,
        };
        toast.Show();
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, s_fade));
        var timer = new DispatcherTimer { Interval = s_toastTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, s_fade);
            fade.Completed += (_, _) => toast.Close();
            toast.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = SetWindowLongPtrW(
            handle,
            ExtendedStyleIndex,
            GetWindowLongPtrW(handle, ExtendedStyleIndex)
                | ClickThrough
                | ToolWindow
                | NeverActivates
        );
        _ = SetWindowDisplayAffinity(handle, ExcludeFromCapture);

        // Moving There First Settles The DPI, Then The Size
        for (var pass = 0; pass < 2; pass++)
        {
            _ = SetWindowPos(
                handle,
                s_topMost,
                _monitor.Left,
                _monitor.Top,
                _monitor.Width,
                _height,
                NoActivate
            );
        }
    }

    private static Grid Frame(MonitorInfo monitor, bool isChosen)
    {
        var palette = AppTheme.Custom.PaletteDark;
        var name = new TextBlock
        {
            Text = monitor.Label,
            Foreground = BrushOf(isChosen ? palette.Black : palette.TextPrimary),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = NameSize,
            FontWeight = FontWeights.SemiBold,
        };
        var edge = new Border
        {
            BorderBrush = BrushOf(isChosen ? palette.Info : palette.TextDisabled),
            BorderThickness = new Thickness(EdgeThickness),
        };
        return new Grid
        {
            Children = { edge, Badge(name, isChosen ? palette.Info : palette.AppbarBackground) },
        };
    }

    private static Border Badge(TextBlock words, MudColor background)
    {
        return new Border
        {
            Child = words,
            Background = BrushOf(background),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 40, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private static SolidColorBrush BrushOf(MudColor color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int left,
        int top,
        int width,
        int height,
        uint flags
    );
}
