using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LetsClip.Data.ViewModels;

namespace LetsClip.Desktop.Interop;

public static partial class WindowPlacement
{
    private const int ShowNormal = 1;
    private const int ShowWithoutFocus = 4;
    private const int ShowMaximized = 3;
    private const int RestoreToMaximized = 2;

    // The Smallest Stored Size Taken Back
    private const int MinimumSide = 200;

    public static WindowBounds? Read(Window window)
    {
        // The Restored Size, Also While Maximised Or Minimised
        var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement))
        {
            return null;
        }

        var isMaximized =
            window.WindowState == WindowState.Maximized
            || (
                window.WindowState == WindowState.Minimized
                && (placement.Flags & RestoreToMaximized) != 0
            );
        return new WindowBounds(
            placement.Left,
            placement.Top,
            placement.Right,
            placement.Bottom,
            isMaximized
        );
    }

    public static void Apply(Window window, WindowBounds bounds)
    {
        // Called From SourceInitialized, Windows Keeps It On A Screen
        if (bounds.Right - bounds.Left < MinimumSide || bounds.Bottom - bounds.Top < MinimumSide)
        {
            return;
        }

        var placement = new Placement
        {
            Length = Marshal.SizeOf<Placement>(),
            ShowCommand =
                bounds.IsMaximized ? ShowMaximized
                : window.ShowActivated ? ShowNormal
                : ShowWithoutFocus,
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Bottom,
            MinimizedX = -1,
            MinimizedY = -1,
            MaximizedX = -1,
            MaximizedY = -1,
        };
        var handle = new WindowInteropHelper(window).Handle;
        if (!SetWindowPlacement(handle, ref placement) || bounds.IsMaximized)
        {
            return;
        }

        // WPF Keeps Its Own Numbers, Told What Windows Placed
        if (GetWindowRect(handle, out var placed))
        {
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            window.Left = placed.Left / scale;
            window.Top = placed.Top / scale;
            window.Width = (placed.Right - placed.Left) / scale;
            window.Height = (placed.Bottom - placed.Top) / scale;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public int MinimizedX;
        public int MinimizedY;
        public int MaximizedX;
        public int MaximizedY;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rectangle rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint window, ref Placement placement);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint window, ref Placement placement);
}
