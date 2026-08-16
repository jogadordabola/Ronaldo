using System;
using System.Windows;

namespace ronaldo;

/// <summary>
/// Saves and restores where the window was last placed, including on a secondary monitor.
/// WPF's WindowStartupLocation always reopens on the primary screen, so placement is applied
/// manually and sanity-checked against the desktop that exists right now.
/// </summary>
public static class WindowPlacement
{
    public static void Restore(Window window, AppSettings settings)
    {
        if (settings.WindowWidth is > 0) window.Width = settings.WindowWidth.Value;
        if (settings.WindowHeight is > 0) window.Height = settings.WindowHeight.Value;

        if (settings.WindowLeft is not { } left || settings.WindowTop is not { } top)
            return;

        // A monitor may have been unplugged or rearranged since the last run; only honour a
        // position that still lands on the desktop, otherwise the window opens off-screen.
        if (!IsReachable(left, top, window.Width))
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;

        if (settings.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    public static void Capture(Window window, AppSettings settings)
    {
        settings.WindowMaximized = window.WindowState == WindowState.Maximized;

        // RestoreBounds holds the un-maximized rect, which is what we want to reopen at.
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top) ||
            double.IsInfinity(bounds.Left) || double.IsInfinity(bounds.Top)) return;

        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
    }

    /// <summary>
    /// True when enough of the title bar would sit inside the virtual desktop to grab it.
    /// SystemParameters reports device-independent units, matching Window.Left/Top, so this
    /// stays correct when monitors run at different scaling factors.
    /// </summary>
    private static bool IsReachable(double left, double top, double width)
    {
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var titleBar = new Rect(left, top, Math.Max(width, 1), 40);
        titleBar.Intersect(desktop);

        return titleBar is { Width: > 80, Height: > 10 };
    }
}
