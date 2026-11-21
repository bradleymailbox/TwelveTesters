using System.Text.Json;

namespace TwelveTestersTray;

public class Settings
{
    public int IntervalMinutes { get; set; } = 15;
    public string LastEveningReminder { get; set; } = "";
    public bool DeviceChecks { get; set; } = true;
    /// <summary>Keys of page sections the user collapsed (Today: to-do, swaps; Settings groups).</summary>
    public List<string> CollapsedSections { get; set; } = new();
    /// <summary>To-do items the user marked as done: item key → fingerprint (priority + title) when dismissed.
    /// An item stays hidden until it disappears from the site or its fingerprint changes; keys already carry the
    /// state that matters (counts, dates), so a changed key is a new item.</summary>
    public Dictionary<string, string> DismissedTodos { get; set; } = new();
    public string? AdbPath { get; set; }
    /// <summary>Session URL → Play package name (join links disappear once a session moves on, so cache them).</summary>
    public Dictionary<string, string> PackageBySession { get; set; } = new();

    /// <summary>Daily "open and test" reminder times (HH:mm). Empty = reminders off.</summary>
    public List<string> ReminderTimes { get; set; } = new() { "10:00", "19:00" };
    /// <summary>"yyyy-MM-dd HH:mm" of the last reminder slot shown, so each fires once.</summary>
    public string LastReminderSlot { get; set; } = "";
    public DateTime? SnoozeUntil { get; set; }
    /// <summary>While "testing", how often to screenshot the phone.</summary>
    public int CaptureIntervalSeconds { get; set; } = 10;
    /// <summary>Date (yyyy-MM-dd) → session URLs you confirmed you tested that day.</summary>
    public Dictionary<string, List<string>> TestedLog { get; set; } = new();
    public string Theme { get; set; } = "Dark";
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public WindowBounds? WindowBounds { get; set; }
    public string LastPage { get; set; } = "Today";

    public HashSet<string> ConfirmedOn(DateTime day) =>
        TestedLog.TryGetValue(day.ToString("yyyy-MM-dd"), out var l) ? l.ToHashSet() : new();

    public void Confirm(string sessionUrl)
    {
        var key = DateTime.Today.ToString("yyyy-MM-dd");
        if (!TestedLog.TryGetValue(key, out var l)) TestedLog[key] = l = new();
        if (!l.Contains(sessionUrl)) l.Add(sessionUrl);
        // keep ~30 days of history
        foreach (var old in TestedLog.Keys.Where(k => string.CompareOrdinal(k, DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd")) < 0).ToList())
            TestedLog.Remove(old);
        Save();
    }

    public void Unconfirm(string sessionUrl)
    {
        if (TestedLog.TryGetValue(DateTime.Today.ToString("yyyy-MM-dd"), out var l) && l.Remove(sessionUrl)) Save();
    }

    /// <summary>All app data lives under this root (settings, login, log, WebView2 profile, screenshots).</summary>
    public const string DataRoot = @"C:\bDevStudios\00_Backups\TwelveTesters";
    public static string Dir => Path.Combine(DataRoot, "AppData");
    public static string ScreenshotsDir => Path.Combine(DataRoot, "Screenshots");

    static string LegacyDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwelveTestersTray");

    /// <summary>One-time copy (never a move) of the old %LOCALAPPDATA% data. settings.json is copied last,
    /// so an interrupted copy is retried on the next start.</summary>
    static Settings()
    {
        try
        {
            if (File.Exists(Path.Combine(Dir, "settings.json")) || !Directory.Exists(LegacyDir)) return;
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ScreenshotsDir);
            CopyTree(Path.Combine(LegacyDir, "captures"), ScreenshotsDir);
            CopyTree(Path.Combine(LegacyDir, "WebView2"), Path.Combine(Dir, "WebView2"));
            foreach (var name in new[] { "login.dat", "log.txt", "settings.json" })
                if (File.Exists(Path.Combine(LegacyDir, name)))
                    File.Copy(Path.Combine(LegacyDir, name), Path.Combine(Dir, name), overwrite: false);
        }
        catch { /* start with defaults; the old data is untouched */ }
    }

    static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { if (!File.Exists(target)) File.Copy(file, target); } catch (IOException) { /* locked, e.g. a live browser profile */ }
        }
    }

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* settings are a convenience; ignore write failures */ }
    }
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public class SessionInfo
{
    public string Url { get; set; } = "";
    public string Partner { get; set; } = "";
    public string DaysLeft { get; set; } = "";
    public string State { get; set; } = "";
    public string App { get; set; } = "";
    public string MyStatus { get; set; } = "";
    public string PartnerStatus { get; set; } = "";
    public string? Package { get; set; }

    public override string ToString() => App;

    /// <summary>Session id (also used by the check-in and feedback pages).</summary>
    public string Id => Url.TrimEnd('/').Split('/').Last();

    /// <summary>The site's "submit report" page for this app (same id as the session).</summary>
    public string FeedbackUrl =>
        "https://www.twelvetesters.com/dashboard/feedback/submit/" + Url.TrimEnd('/').Split('/').Last();
}

public class RequestInfo
{
    public string From { get; set; } = "";
    public string TheirApp { get; set; } = "";
}

public class DashboardInfo
{
    public int? Credits { get; set; }
    public int? DueToday { get; set; }
    public int? FeedbackIn { get; set; }
    public int? RequestsIn { get; set; }
    public bool LoggedIn { get; set; }
}

public class FeedbackDraft
{
    /// <summary>Site values: BUG, CRASH, UI_UX, PERFORMANCE, FEATURE_SUGGESTION, GENERAL, OTHER.</summary>
    public string Type { get; set; } = "BUG";
    /// <summary>LOW, MEDIUM, HIGH, CRITICAL.</summary>
    public string Severity { get; set; } = "MEDIUM";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

/// <summary>What the site said after "Check screenshot" (or why we couldn't get that far).</summary>
public class InstallCheckResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    /// <summary>"join" when the site still wants "I joined the test"; "done" when already checked in today.</summary>
    public string Stage { get; set; } = "";
    /// <summary>e.g. "High confidence".</summary>
    public string Confidence { get; set; } = "";
    /// <summary>The site's check lines (clock, device time, freshness…).</summary>
    public string Details { get; set; } = "";
    /// <summary>True when the site offers "Confirm check-in".</summary>
    public bool CanConfirm { get; set; }
}

public class CheckinList
{
    public List<string> Due { get; set; } = new();
    public List<string> Done { get; set; } = new();
}

public class ApiLoginResult
{
    public int Status { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Simple diagnostic log (never contains the password).</summary>
public static class Log
{
    static string FilePath => Path.Combine(Settings.Dir, "log.txt");

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* logging must never break the app */ }
    }
}

public class Snapshot
{
    public DateTime At { get; set; } = DateTime.Now;
    public bool SignedIn { get; set; }
    public bool HasSavedLogin { get; set; }
    public string? LoginError { get; set; }
    public int? Credits { get; set; }
    public int DueToday { get; set; }
    public int CheckinsDue { get; set; }
    /// <summary>Session ids whose daily check-in on the site is still due today / already done today.</summary>
    public HashSet<string> SiteCheckinDue { get; set; } = new();
    public HashSet<string> SiteCheckinDone { get; set; } = new();
    public int FeedbackIn { get; set; }
    public List<SessionInfo> Sessions { get; set; } = new();
    public List<RequestInfo> Requests { get; set; } = new();
    /// <summary>Null when phone checks are turned off.</summary>
    public DeviceStatus? Device { get; set; }
    /// <summary>Session URLs you've confirmed testing today.</summary>
    public HashSet<string> ConfirmedToday { get; set; } = new();

    /// <summary>All active swaps (including ones still in setup).</summary>
    public IEnumerable<SessionInfo> ActiveSessions => Sessions.Where(x =>
        string.IsNullOrEmpty(x.State) || x.State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase));

    public static bool IsSetupStage(SessionInfo x) =>
        x.MyStatus.Equals("Get started", StringComparison.OrdinalIgnoreCase) ||
        x.MyStatus.Equals("Join the test", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sessions past setup, i.e. ones that need opening and testing every day.</summary>
    public IEnumerable<SessionInfo> DailySessions => Sessions.Where(x =>
        (string.IsNullOrEmpty(x.State) || x.State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)) &&
        !x.MyStatus.Equals("Get started", StringComparison.OrdinalIgnoreCase) &&
        !x.MyStatus.Equals("Join the test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(x.MyStatus));
}

public enum Priority { High, Normal, Low }

public class TodoItem
{
    public string Key { get; init; } = "";
    public Priority Priority { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Web URL to open, or "app:signin" to open the sign-in window.</summary>
    public string Url { get; init; } = "";
}
