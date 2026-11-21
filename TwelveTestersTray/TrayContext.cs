using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TwelveTestersTray.Services;
using TwelveTestersTray.ViewModels;

namespace TwelveTestersTray;

public class TrayContext : ApplicationContext
{
    public Settings Settings => settings;
    public Snapshot? Snapshot => last;
    public IReadOnlyList<TodoItem> Todos => todos;
    public CaptureManager Capture => capture;
    public AdbService Adb => adb;
    public string? Error => lastError;
    public bool IsChecking => checkCommand.IsRunning;
    public bool IsStarting { get; private set; } = true;
    public bool BrowserRuntimeMissing { get; private set; }
    public bool IsManualSignInOpen { get => browser.IsSignInOpen; set => browser.IsSignInOpen = value; }
    public bool ReportRunning { get; private set; }
    public Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> BrowserEnvironment => browser.EnvironmentTask;
    public event Action? StateChanged;

    readonly NotifyIcon tray = new();
    readonly SiteBrowser browser = new();
    readonly CaptureManager capture;
    readonly AdbService adb;
    readonly System.Windows.Forms.Timer timer = new();
    readonly System.Windows.Forms.Timer reminderTimer = new() { Interval = 30_000 };
    /// <summary>Phone-only refresh between site checks, so a fresh install shows up within a minute.</summary>
    readonly System.Windows.Forms.Timer phoneTimer = new() { Interval = 60_000 };
    readonly AsyncRelayCommand phoneCommand;
    readonly AsyncRelayCommand checkCommand;
    (string Page, SessionInfo? Session) balloonTarget = ("Today", null);
    readonly Settings settings = Settings.Load();

    Snapshot? last;
    List<TodoItem> todos = new();
    HashSet<string> seenKeys = new();
    string? lastError;
    bool firstCheck = true;
    bool loginFailed;       // stop retrying a bad saved login until it's changed (avoids lockouts)
    bool promptedAccount;   // only auto-open the login dialog once per run
    Icon? currentIcon;


    public TrayContext()
    {
        checkCommand = new AsyncRelayCommand(CheckNowCoreAsync);
        phoneCommand = new AsyncRelayCommand(RefreshPhoneCoreAsync);
        checkCommand.PropertyChanged += (_, _) => Refresh();
        adb = new AdbService(settings.AdbPath);
        _ = adb.RefreshPathAsync();
        capture = new CaptureManager(adb, () => settings.CaptureIntervalSeconds);
        CaptureManager.CleanupOld();

        BuildMenu();
        SetIcon(0, unknown: true);
        tray.Text = "Twelve Testers — starting…";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowList(); };
        tray.BalloonTipClicked += (_, _) =>
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow window)
                window.ShowFromTray(balloonTarget.Page, balloonTarget.Session);
        };



        timer.Tick += (_, _) => _ = CheckNowAsync();
        ApplyInterval();

        phoneTimer.Tick += (_, _) => _ = RefreshPhoneAsync();
        phoneTimer.Start();
        capture.EndedAutomatically += run =>
        {
            Balloon($"Testing {run.Session.App} stopped",
                $"{run.Problem} {run.Count} screenshot(s) saved — open Testing for follow-up actions.");
            balloonTarget = ("Testing", run.Session);
            StateChanged?.Invoke();
        };
        capture.Changed += _ => StateChanged?.Invoke();
        reminderTimer.Tick += (_, _) => CheckReminder();
        reminderTimer.Start();

        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try
        {

            await browser.InitAsync();
            await CheckNowAsync();
        }
        catch (Exception ex)
        {
            BrowserRuntimeMissing = IsRuntimeMissing(ex);
            lastError = "Couldn't start the embedded browser (WebView2): " + ex.Message;
            Refresh();
        }
        finally { IsStarting = false; Refresh(); }
    }

    private static bool IsRuntimeMissing(Exception error) =>
        error is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException ||
        (error.InnerException != null && IsRuntimeMissing(error.InnerException));

    // ------------------------------------------------------------------ menu

    void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var show = new ToolStripMenuItem("Open", null, (_, _) => ShowList())
        { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        menu.Items.Add(show);
        menu.Items.Add("Today", null, (_, _) => ShowList());
        menu.Items.Add("Testing", null, (_, _) =>
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow window) window.ShowFromTray("Testing");
        });
        var testingItem = menu.Items[2];
        menu.Opening += (_, _) => testingItem.Visible = capture.Current?.Running == true;
        menu.Items.Add("Check now", null, (_, _) => _ = CheckNowAsync());
        menu.Items.Add("Test next app", null, async (_, _) => await TestNextAsync());
        menu.Items.Add("Open Twelve Testers website", null, (_, _) => OpenUrl(SiteBrowser.Base + "/dashboard"));
        menu.Items.Add("Settings", null, (_, _) =>
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow window) window.ShowFromTray("Settings");
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        tray.ContextMenuStrip = menu;
    }

    void ApplyInterval()
    {
        timer.Interval = Math.Max(1, settings.IntervalMinutes) * 60_000;
        timer.Start();
    }

    public void ApplySettings() => ApplyInterval();
    public void RefreshFromSettings() => RebuildTodos();
    public void ShowNotice(string text)
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow window) window.ShowNotice(text);
    }
    public void OnHiddenToTray()
    {
        if (settings.TrayHintShown) return;
        Balloon("Twelve Testers", "Still running in the tray. Click the tray icon to reopen.");
        settings.TrayHintShown = true;
        settings.Save();
    }
    async Task TestNextAsync()
    {
        var next = last?.DailySessions.FirstOrDefault(s => s.Package != null && !settings.ConfirmedOn(DateTime.Today).Contains(s.Url));
        if (next == null) { ShowList(); ShowNotice("No untested app with a known package is available."); return; }
        var error = await capture.StartAsync(next);
        if (System.Windows.Application.Current.MainWindow is MainWindow window) window.ShowFromTray("Testing", next);
        if (error != null) ShowNotice(error);
    }
    public void ShowManualSignIn()
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.BeginManualSignIn();
    }
    public void ResetLoginFailure() => loginFailed = false;
    public async Task<string?> SubmitFeedbackAsync(SessionInfo session, FeedbackDraft draft)
    {
        if (ReportRunning) return "A report is already being submitted.";
        ReportRunning = true;
        try { await browser.InitAsync(); return await browser.SubmitFeedbackAsync(session, draft); }
        finally { ReportRunning = false; }
    }
    public Task<InstallCheckResult> BeginInstallCheckinAsync(SessionInfo session, string file, DateTime capturedAt, bool joined) =>
        browser.BeginInstallCheckinAsync(session, file, capturedAt, joined);
    public Task<InstallCheckResult> BeginDailyCheckinAsync(SessionInfo session, Func<Task<(string? file, DateTime capturedAt, string? error)>> captureShot) =>
        browser.BeginDailyCheckinAsync(session, captureShot);
    public Task<string?> ConfirmCheckinAsync() => browser.ConfirmInstallCheckinAsync();
    public void CancelCheckin() => browser.CancelInstallCheckin();
    public void RecordCheckin(SessionInfo session)
    {
        settings.Confirm(session.Url);
        if (last != null) { last.SiteCheckinDue.Remove(session.Id); last.SiteCheckinDone.Add(session.Id); }
        RebuildTodos();
        _ = CheckNowAsync();
    }

    // ---------------------------------------------------------------- checks

    public Task CheckNowAsync() => checkCommand.ExecuteAsync();

    async Task CheckNowCoreAsync()
    {
        if (browser.IsSignInOpen || IsManualSignInOpen) return;
        Refresh();
        try
        {
            await browser.InitAsync();
            BrowserRuntimeMissing = false;
            var snap = await browser.ScrapeAsync(() => loginFailed ? null : CredentialStore.Load(),
                                                 settings.PackageBySession);
            snap.HasSavedLogin = CredentialStore.Exists;
            // A check-in done on the site counts as "tested today" here too.
            foreach (var x in snap.Sessions.Where(x => snap.SiteCheckinDone.Contains(x.Id)))
                if (!settings.ConfirmedOn(DateTime.Today).Contains(x.Url)) settings.Confirm(x.Url);
            snap.ConfirmedToday = settings.ConfirmedOn(DateTime.Today);
            settings.Save(); // persist any newly discovered package names

            if (settings.DeviceChecks && snap.SignedIn)
            {
                try
                {
                    var pkgs = snap.Sessions.Select(x => x.Package).OfType<string>().ToList();
                    snap.Device = await adb.CheckAsync(pkgs);
                    var d = snap.Device;
                    Log.Write(d.Connected
                        ? $"Phone {d.Name}: " + string.Join(", ", pkgs.Select(p =>
                            $"{p}={(d.Installed.Contains(p) ? "installed" : "MISSING")}{(d.OpenedToday(p) ? "+opened today" : "")}"))
                        : "Phone: " + d.Problem);
                }
                catch (Exception ex)
                {
                    snap.Device = new DeviceStatus { Problem = "Phone check failed: " + ex.Message };
                    Log.Write("Phone check failed: " + ex);
                }
            }
            if (snap.LoginError != null) loginFailed = true;
            last = snap;
            lastError = null;
            var newTodos = TodoBuilder.Build(snap);
            Notify(newTodos);
            todos = newTodos;
        }
        catch (Exception ex)
        {
            BrowserRuntimeMissing = IsRuntimeMissing(ex);
            lastError = ex.Message;
            Log.Write("Check failed: " + ex);
        }
        finally
        {
            Refresh();
        }
        if (last != null)
            Log.Write($"Check: signedIn={last.SignedIn} sessions={last.Sessions.Count} requests={last.Requests.Count} loginError={last.LoginError ?? "-"}");

        // First run with nothing saved: ask for the login once.
        if (last is { SignedIn: false } && !CredentialStore.Exists && !promptedAccount)
        {
            promptedAccount = true;
            ShowAccount();
        }
    }

    public void ShowAccount()
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.ShowFromTray("Account");
    }

    void Notify(List<TodoItem> newTodos)
    {
        var keys = newTodos.Select(t => t.Key).ToHashSet();
        var fresh = newTodos.Where(t => !seenKeys.Contains(t.Key) && t.Priority != Priority.Low).ToList();

        if (firstCheck)
        {
            int n = newTodos.Count(t => t.Priority != Priority.Low);
            if (n > 0) Balloon("Twelve Testers", $"You have {n} thing{(n == 1 ? "" : "s")} to do. Click to see them.");
            firstCheck = false;
        }
        else if (fresh.Count > 0)
        {
            var lines = fresh.Take(3).Select(t => "• " + t.Title);
            var more = fresh.Count > 3 ? $"\n…and {fresh.Count - 3} more" : "";
            Balloon("Twelve Testers — new to do", string.Join("\n", lines) + more);
        }

        // (Daily testing nudges are handled by the scheduled reminder — see CheckReminder.)
        seenKeys = keys;
    }

    void Balloon(string title, string text)
    {
        balloonTarget = ("Today", null);
        tray.BalloonTipTitle = title;
        tray.BalloonTipText = text;
        tray.BalloonTipIcon = ToolTipIcon.Info;
        tray.ShowBalloonTip(8000);
    }

    void Refresh()
    {
        if (stopped) return;
        int count = todos.Count(t => t.Priority != Priority.Low);
        bool unknown = last == null || lastError != null;
        SetIcon(count, unknown);

        string tip = IsChecking ? "Twelve Testers — checking…"
            : lastError != null ? "Twelve Testers — last check failed"
            : last == null ? "Twelve Testers"
            : !last.SignedIn ? (last.LoginError != null ? "Twelve Testers — login failed" : "Twelve Testers — login needed")
            : count == 0 ? "Twelve Testers — all done"
            : $"Twelve Testers — {count} to do";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;

        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------- actions

    // ---------------------------------------------------------- reminders

    int PendingTests() => last is { SignedIn: true }
        ? last.DailySessions.Count(x => !settings.ConfirmedOn(DateTime.Today).Contains(x.Url) || last.SiteCheckinDue.Contains(x.Id))
        : 0;

    void CheckReminder()
    {
        if (last is not { SignedIn: true }) return;
        var now = DateTime.Now;

        if (settings.SnoozeUntil is DateTime until)
        {
            if (now < until) return;
            settings.SnoozeUntil = null;
            settings.Save();
            if (PendingTests() > 0) ShowReminder(auto: true);
            return;
        }

        // Latest reminder time today that has passed (ignore ones more than 3h old, e.g. PC was off).
        var slot = settings.ReminderTimes
            .Select(t => TimeSpan.TryParse(t, out var ts) ? ts : (TimeSpan?)null)
            .OfType<TimeSpan>()
            .Where(ts => now.TimeOfDay >= ts && now.TimeOfDay - ts < TimeSpan.FromHours(3))
            .DefaultIfEmpty(TimeSpan.MinValue)
            .Max();
        if (slot == TimeSpan.MinValue) return;

        var slotKey = $"{now:yyyy-MM-dd} {slot:hh\\:mm}";
        if (settings.LastReminderSlot == slotKey) return;
        settings.LastReminderSlot = slotKey;
        settings.Save();

        if (PendingTests() > 0) ShowReminder(auto: true);
    }

    public void ShowReminder(bool auto)
    {
        _ = RefreshPhoneAsync(); // don't show stale "not installed"
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.ShowFromTray("Today");
        if (auto)
        {
            int n = PendingTests();
            Balloon("Twelve Testers — time to test",
                $"Open and use {n} swap app{(n == 1 ? "" : "s")} on your phone, then confirm.");
        }
    }

    void RebuildTodos()
    {
        if (last == null) return;
        last.ConfirmedToday = settings.ConfirmedOn(DateTime.Today);
        todos = TodoBuilder.Build(last);
        seenKeys.UnionWith(todos.Select(t => t.Key)); // confirmations shouldn't trigger "new" popups
        Refresh();
    }

    // ----------------------------------------------------- testing & feedback

    public Task ShowFeedbackAsync(SessionInfo x)
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.ShowFromTray("Report", x);
        return Task.CompletedTask;
    }

    // ------------------------------------------------- install check-in & phone

    void ShowInstallCheckin(SessionInfo x) => ShowCheckin(x, daily: false);

    public void ShowCheckin(SessionInfo x, bool daily)
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.ShowFromTray("Checkin", x);
    }

    /// <summary>
    /// Re-reads which apps are installed on the phone (cheap adb call) without re-reading the site,
    /// and offers the install check-in as soon as a setup-stage app appears.
    /// </summary>
    Task RefreshPhoneAsync() => phoneCommand.ExecuteAsync();

    async Task RefreshPhoneCoreAsync()
    {
        if (IsChecking || !settings.DeviceChecks || last is not { SignedIn: true }) return;
        try
        {
            var snap = last;
            var installed = await adb.InstalledPackagesAsync();
            if (installed == null || snap != last) return; // phone gone, or a full check replaced the snapshot

            var before = snap.Device is { Connected: true } d ? d.Installed : null;
            snap.Device ??= new DeviceStatus { AdbFound = true };
            snap.Device.Connected = true;
            snap.Device.Problem = null;
            snap.Device.Installed = installed;
            if (before != null && before.SetEquals(installed)) return;

            var newlyInstalled = snap.ActiveSessions
                .Where(x => x.Package != null && installed.Contains(x.Package) && before?.Contains(x.Package) == false)
                .ToList();
            Log.Write("Phone refresh: installed set changed" +
                      (newlyInstalled.Count > 0 ? "; new: " + string.Join(", ", newlyInstalled.Select(x => x.Package)) : ""));

            RebuildTodos();

            var setup = newlyInstalled.Where(Snapshot.IsSetupStage).ToList();
            if (setup.Count > 0)
            {
                var x = setup[0];
                Balloon($"{x.App} is installed",
                    "Click here to upload the install screenshot (Day 1 check-in)." +
                    (setup.Count > 1 ? $" {setup.Count - 1} more new install(s) are in the menu." : ""));
                balloonTarget = ("Checkin", x);
            }
        }
        catch (Exception ex) { Log.Write("Phone refresh failed: " + ex.Message); }
    }

    public void ShowList()
    {
        _ = RefreshPhoneAsync();
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.ShowFromTray("Today");
    }

    public void ShowTaskList() => ShowList();

    public void OpenItem(TodoItem t)
    {
        if (string.IsNullOrEmpty(t.Url)) return;
        if (t.Url == "app:test") ShowReminder(auto: false);
        else if (t.Url == "app:account") ShowAccount();
        else if (t.Url == "app:signin") ShowManualSignIn();
        else if (t.Url.StartsWith("app:install:") || t.Url.StartsWith("app:checkin:"))
        {
            bool daily = t.Url.StartsWith("app:checkin:");
            var url = t.Url[(daily ? "app:checkin:" : "app:install:").Length..];
            var x = last?.Sessions.FirstOrDefault(s => s.Url == url);
            if (x != null) ShowCheckin(x, daily); else OpenUrl(url);
        }
        else OpenUrl(t.Url);
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no default browser configured */ }
    }

    bool stopped;

    public void Stop()
    {
        if (stopped) return;
        stopped = true;
        capture.Stop();
        timer.Stop();
        reminderTimer.Stop();
        phoneTimer.Stop();
        browser.CancelInstallCheckin();
        tray.Visible = false;
        tray.Dispose();
        browser.Dispose();
        currentIcon?.Dispose();
    }

    public void ExitApplication(bool confirmed = false)
    {
        if (!confirmed && (capture.Current?.Running == true || ReportRunning))
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow active)
            {
                active.ShowFromTray(ReportRunning ? "Report" : "Testing");
                active.ShowNotice("Testing or a report submission is running. Exiting will interrupt it.", true);
            }
            return;
        }
        Stop();
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.AllowClose = true;
        System.Windows.Application.Current.Shutdown();
    }

    void Exit() => ExitApplication();

    // ---------------------------------------------------------------- icon

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);

    void SetIcon(int count, bool unknown)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var baseIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (baseIcon is not null) g.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            var bg = unknown ? Color.FromArgb(120, 120, 120)
                : count > 0 ? Color.FromArgb(214, 84, 30)
                : Color.FromArgb(40, 150, 70);
            using var brush = new SolidBrush(bg);
            g.FillEllipse(brush, 11, 11, 20, 20);

            string text = unknown ? "?" : count == 0 ? "✓" : count > 9 ? "9+" : count.ToString();
            using var font = new Font("Segoe UI", text.Length > 1 ? 10 : 14, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(11, 11, 20, 20), sf);
        }

        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);

        tray.Icon = icon;
        currentIcon?.Dispose();
        currentIcon = icon;
    }
}

