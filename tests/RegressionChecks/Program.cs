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

        TodoItem Todo(string key, string title, Priority priority = Priority.High) => new() { Key = key, Title = title, Priority = priority };
        var feedback = Todo("feedback-in:4", "Read 4 feedback reports");
        var join = Todo("join:s1", "Join the Aquatinta test");
        var dismissals = new Dictionary<string, string> { [feedback.Key] = TodoBuilder.Fingerprint(feedback) };
        var (visible, changed) = TodoBuilder.ApplyDismissals([feedback, join], dismissals, fullCheck: false, signedIn: true);
        Check(visible.Count == 1 && visible[0].Key == join.Key && !changed, "Dismissed to-do stays hidden while unchanged");
        (visible, changed) = TodoBuilder.ApplyDismissals([Todo(feedback.Key, "Read 4 feedback reports", Priority.Normal), join], dismissals, false, true);
        Check(visible.Count == 2 && changed && dismissals.Count == 0, "Dismissed to-do returns when its priority changes");
        dismissals[feedback.Key] = TodoBuilder.Fingerprint(feedback);
        (visible, changed) = TodoBuilder.ApplyDismissals([Todo(feedback.Key, "Read 5 feedback reports"), join], dismissals, false, true);
        Check(visible.Count == 2 && dismissals.Count == 0, "Dismissed to-do returns when its title changes");
        dismissals[feedback.Key] = TodoBuilder.Fingerprint(feedback);
        (visible, changed) = TodoBuilder.ApplyDismissals([join], dismissals, fullCheck: false, signedIn: true);
        Check(dismissals.ContainsKey(feedback.Key) && !changed, "Dismissal survives a partial rebuild that omits the item");
        (visible, changed) = TodoBuilder.ApplyDismissals([join], dismissals, fullCheck: true, signedIn: false);
        Check(dismissals.ContainsKey(feedback.Key), "Dismissal survives a check where sign-in failed");
        (visible, changed) = TodoBuilder.ApplyDismissals([join], dismissals, fullCheck: true, signedIn: true);
        Check(dismissals.Count == 0 && changed, "Dismissal is forgotten once a full check no longer lists the item");
        (visible, changed) = TodoBuilder.ApplyDismissals([feedback, join], dismissals, true, true);
        Check(visible.Count == 2, "The same item reappears after it went away and came back");

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
