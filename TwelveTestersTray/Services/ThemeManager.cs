using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TwelveTestersTray.Services;

/// <summary>Changes the application palette and matches Windows title bars.</summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public static readonly IReadOnlyList<string> Themes = [Dark, Light];
    public static string Current { get; private set; } = Dark;
    public static event Action? Changed;
    private static bool hooked;

    public static string Normalize(string? theme) =>
        string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    public static void Apply(string? theme)
    {
        var name = Normalize(theme);
        var app = System.Windows.Application.Current;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/TwelveTestersTray;component/Themes/{name}.xaml", UriKind.Absolute) };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0 && IsPalette(merged[0])) merged[0] = palette;
        else merged.Insert(0, palette);
        Current = name;

        if (!hooked)
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => SetTitleBar((Window)s)));
            hooked = true;
        }
        foreach (Window window in app.Windows) SetTitleBar(window);
        Changed?.Invoke();
    }

    private static bool IsPalette(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? "";
        return source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void SetTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = Current == Dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); }
        catch { /* Older Windows keeps the system title bar colour. */ }
    }
}
