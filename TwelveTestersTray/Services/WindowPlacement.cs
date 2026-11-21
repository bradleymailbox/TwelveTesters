using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TwelveTestersTray.Services;

/// <summary>Stores WPF restore bounds, keeping restored windows inside a current monitor work area.</summary>
public static class WindowPlacement
{
    public static Rect Clamp(WindowBounds saved, IReadOnlyList<Rect> workAreas, double minWidth, double minHeight)
    {
        if (workAreas.Count == 0) throw new ArgumentException("A monitor work area is required.", nameof(workAreas));
        var validPosition = double.IsFinite(saved.Left) && double.IsFinite(saved.Top);
        var width = double.IsFinite(saved.Width) && saved.Width > 0 ? saved.Width : 1100;
        var height = double.IsFinite(saved.Height) && saved.Height > 0 ? saved.Height : 760;
        var requested = new Rect(validPosition ? saved.Left : workAreas[0].Left,
            validPosition ? saved.Top : workAreas[0].Top, width, height);
        var area = workAreas.OrderByDescending(a => IntersectionArea(a, requested))
            .ThenBy(a => DistanceSquared(a, requested)).First();
        // On an unusually small display, visibility takes precedence over the normal minimum.
        width = Math.Clamp(width, Math.Min(minWidth, area.Width), area.Width);
        height = Math.Clamp(height, Math.Min(minHeight, area.Height), area.Height);
        return new Rect(Math.Clamp(requested.Left, area.Left, area.Right - width),
            Math.Clamp(requested.Top, area.Top, area.Bottom - height), width, height);
    }

    private static double IntersectionArea(Rect a, Rect b)
    {
        a.Intersect(b);
        return a.IsEmpty ? 0 : a.Width * a.Height;
    }

    private static double DistanceSquared(Rect a, Rect b)
    {
        var x = b.Left - Math.Clamp(b.Left, a.Left, a.Right);
        var y = b.Top - Math.Clamp(b.Top, a.Top, a.Bottom);
        return x * x + y * y;
    }

    public static void Restore(Window window, WindowBounds? saved)
    {
        if (saved == null) return;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var areas = System.Windows.Forms.Screen.AllScreens.Select(screen =>
        {
            var area = screen.WorkingArea;
            return new Rect(transform.Transform(new System.Windows.Point(area.Left, area.Top)),
                transform.Transform(new System.Windows.Point(area.Right, area.Bottom)));
        }).ToArray();
        var bounds = Clamp(saved, areas, window.MinWidth, window.MinHeight);
        window.MinWidth = Math.Min(window.MinWidth, bounds.Width);
        window.MinHeight = Math.Min(window.MinHeight, bounds.Height);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        if (saved.Maximized) window.WindowState = WindowState.Maximized;
    }

    public static WindowBounds? Capture(Window window, bool maximized)
    {
        var rect = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
        if (rect.IsEmpty || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Top) ||
            !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height) || rect.Width <= 0 || rect.Height <= 0) return null;
        return new WindowBounds { Left = rect.Left, Top = rect.Top, Width = rect.Width, Height = rect.Height, Maximized = maximized };
    }
}
