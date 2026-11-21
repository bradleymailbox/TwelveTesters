using System.Windows;
using TwelveTestersTray;
using TwelveTestersTray.Services;
using TwelveTestersTray.ViewModels;

internal static class Program
{
    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }

    [STAThread]
    private static void Main()
    {
        var primary = new Rect(0, 0, 1920, 1040);
        var secondary = new Rect(-1920, 0, 1920, 1040);
        var saved = new WindowBounds { Left = -1800, Top = 100, Width = 1100, Height = 760 };
        var kept = WindowPlacement.Clamp(saved, [primary, secondary], 780, 560);
        Check(kept.Left == -1800 && secondary.Contains(kept), "Keep a valid secondary-monitor position");
        var moved = WindowPlacement.Clamp(saved, [primary], 780, 560);
        Check(primary.Contains(moved), "Recover a window after its monitor is disconnected");
        saved = new WindowBounds { Left = 1800, Top = 990, Width = 3000, Height = 2000 };
        Check(primary.Contains(WindowPlacement.Clamp(saved, [primary], 780, 560)), "Clamp oversized and partially off-screen bounds");
        saved = new WindowBounds { Left = double.NaN, Top = double.PositiveInfinity, Width = -1, Height = 0 };
        Check(primary.Contains(WindowPlacement.Clamp(saved, [primary], 780, 560)), "Recover invalid saved bounds");
        var tiny = new Rect(0, 0, 640, 480);
        Check(tiny.Contains(WindowPlacement.Clamp(saved, [tiny], 780, 560)), "Keep the title bar accessible on a small work area");

        var session = new SessionInfo { Url = "https://example.invalid/session/1", MyStatus = "Get started", Package = "test.app" };
        var snapshot = new Snapshot { SignedIn = true };
        Check(SessionCardState.From(session, snapshot) is { Stage: "Setup", Installed: "Unknown" }, "Disconnected phone does not imply app missing");
        snapshot.Device = new DeviceStatus { Connected = true, AdbFound = true, Installed = ["test.app"] };
        Check(SessionCardState.From(session, snapshot).Stage == "Day 1 check-in", "Installed setup app advances to Day 1 check-in");
        session.MyStatus = "Testing";
        snapshot.ConfirmedToday.Add(session.Url);
        snapshot.SiteCheckinDue.Add(session.Id);
        Check(SessionCardState.From(session, snapshot) is { Stage: "Daily", Checkin: "Due" }, "Mark tested does not hide a due site check-in");
        snapshot.SiteCheckinDue.Clear();
        snapshot.SiteCheckinDone.Add(session.Id);
        Check(SessionCardState.From(session, snapshot).Stage == "Done today", "Completed check-in displays Done today");
        session.Package = null;
        Check(SessionCardState.From(session, snapshot).Installed == "Unknown", "Unknown package does not imply app missing");

        // Construct the window without App.OnStartup, tray services, phone access or saved data writes.
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            ThemeManager.Apply(theme);
            foreach (var resource in new[] { "Chip", "Card", "WarningBrush", "SuccessBrush", "OrangeBrush", "CyanBrush" })
                Check(app.TryFindResource(resource) != null, theme + " resource " + resource);
            window.Measure(new Size(1100, 760));
            window.Arrange(new Rect(0, 0, 1100, 760));
        }
        window.AllowClose = true;
        window.Close();
        Console.WriteLine("PASS: Window layout in both themes; no live services started.");
    }
}
