using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TwelveTestersTray;

public class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "TwelveTestersTray";

    readonly NotifyIcon tray = new();
    readonly BrowserForm browser = new();
    readonly TaskListForm listForm = new();
    readonly CaptureManager capture;
    readonly ReminderForm reminderForm;
    readonly System.Windows.Forms.Timer timer = new();
    readonly System.Windows.Forms.Timer reminderTimer = new() { Interval = 30_000 };
    /// <summary>Phone-only refresh between site checks, so a fresh install shows up within a minute.</summary>
    readonly System.Windows.Forms.Timer phoneTimer = new() { Interval = 60_000 };
    readonly Dictionary<string, InstallCheckinForm> checkinForms = new();
    bool phoneRefreshing;
    Action? balloonAction;  // what clicking the current balloon does (default: show the list)
    readonly Settings settings = Settings.Load();

    Snapshot? last;
    List<TodoItem> todos = new();
    HashSet<string> seenKeys = new();
    string? lastError;
    bool checking;
    bool firstCheck = true;
    bool loginFailed;       // stop retrying a bad saved login until it's changed (avoids lockouts)
    bool promptedAccount;   // only auto-open the login dialog once per run
    bool accountOpen;
    Icon? currentIcon;

    ToolStripMenuItem startupItem = null!;
    ToolStripMenuItem feedbackMenu = null!;
    ToolStripMenuItem intervalMenu = null!;

    public TrayContext()
    {
        capture = new CaptureManager(() => settings.AdbPath, () => settings.CaptureIntervalSeconds);
        reminderForm = new ReminderForm(capture);
        CaptureManager.CleanupOld();

        BuildMenu();
        SetIcon(0, unknown: true);
        tray.Text = "Twelve Testers — starting…";
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowList(); };
        tray.BalloonTipClicked += (_, _) => (balloonAction ?? ShowList)();
        tray.BalloonTipClosed += (_, _) => balloonAction = null;

        listForm.CheckNowClicked += () => _ = CheckNowAsync();
        listForm.ItemOpened += OpenItem;
        browser.SignInWindowClosed += () => _ = CheckNowAsync();

        timer.Tick += (_, _) => _ = CheckNowAsync();
        ApplyInterval();

        reminderForm.ConfirmChanged += (x, on) =>
        {
            if (on) { settings.Confirm(x.Url); Log.Write($"Confirmed tested today: {x.App}"); }
            else settings.Unconfirm(x.Url);
            RebuildTodos();
        };
        reminderForm.StartTesting += async x =>
        {
            var err = await capture.StartAsync(x);
            if (err != null) MessageBox.Show(err, "Start testing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else Balloon($"Testing {x.App}",
                $"Opened on your phone. Screenshotting every {settings.CaptureIntervalSeconds}s while it's on screen.");
        };
        reminderForm.StopTesting += x => { capture.Stop(); AfterTesting(capture.Current); };
        reminderForm.InstallRequested += async x =>
        {
            var err = await Task.Run(() => DeviceChecker.OpenPlayPageAsync(settings.AdbPath, x.Package!));
            if (err != null) MessageBox.Show(err, "Install on phone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else Balloon($"{x.App} opened in Play Store", "Tap Install on your phone. The list updates on the next check.");
        };
        reminderForm.FeedbackRequested += x => _ = ShowFeedbackAsync(x);
        reminderForm.InstallCheckinRequested += ShowInstallCheckin;
        reminderForm.CheckinRequested += x => ShowCheckin(x, daily: true);
        phoneTimer.Tick += (_, _) => _ = RefreshPhoneAsync();
        phoneTimer.Start();
        capture.EndedAutomatically += run =>
            Balloon($"Testing {run.Session.App} stopped",
                $"{run.Problem} {run.Count} screenshot(s) saved — use Give feedback to attach them.");
        reminderForm.SnoozeClicked += () =>
        {
            settings.SnoozeUntil = DateTime.Now.AddMinutes(30);
            settings.Save();
        };
        reminderTimer.Tick += (_, _) => CheckReminder();
        reminderTimer.Start();

        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            browser.Show(); // off-screen; WebView2 needs a real window
            await browser.InitAsync();
            await CheckNowAsync();
        }
        catch (Exception ex)
        {
            lastError = "Couldn't start the embedded browser (WebView2): " + ex.Message;
            Refresh();
        }
    }

    // ------------------------------------------------------------------ menu

    void BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var show = new ToolStripMenuItem("Show what I need to do", null, (_, _) => ShowList())
        { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        menu.Items.Add(show);
        menu.Items.Add("Test && confirm today's apps…", null, (_, _) => ShowReminder(auto: false));
        feedbackMenu = new ToolStripMenuItem("Give feedback on…") { Enabled = false };
        menu.Items.Add(feedbackMenu);
        menu.Items.Add("Check now", null, (_, _) => _ = CheckNowAsync());
        menu.Items.Add("Open Twelve Testers", null, (_, _) => OpenUrl(BrowserForm.Base + "/dashboard"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Login (email && password)…", null, (_, _) => ShowAccount());
        menu.Items.Add("Sign in manually in a window…", null, (_, _) => browser.ShowSignIn());

        intervalMenu = new ToolStripMenuItem("Check every");
        foreach (var m in new[] { 5, 15, 30, 60 })
        {
            var item = new ToolStripMenuItem(m == 60 ? "1 hour" : $"{m} minutes") { Tag = m };
            item.Click += (_, _) => { settings.IntervalMinutes = m; settings.Save(); ApplyInterval(); };
            intervalMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(intervalMenu);

        var remindMenu = new ToolStripMenuItem("Remind me to test at");
        foreach (var t in new[] { "09:00", "10:00", "12:00", "15:00", "18:00", "19:00", "21:00" })
        {
            var item = new ToolStripMenuItem(t) { Checked = settings.ReminderTimes.Contains(t), CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                settings.ReminderTimes.Remove(t);
                if (item.Checked) settings.ReminderTimes.Add(t);
                settings.ReminderTimes.Sort(StringComparer.Ordinal);
                settings.Save();
            };
            remindMenu.DropDownItems.Add(item);
        }
        remindMenu.DropDownItems.Add(new ToolStripSeparator());
        remindMenu.DropDownItems.Add("Turn all off", null, (_, _) =>
        {
            foreach (var i in remindMenu.DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.CheckOnClick)) i.Checked = false;
        });
        menu.Items.Add(remindMenu);

        var shotMenu = new ToolStripMenuItem("While testing, screenshot every");
        foreach (var sec in new[] { 5, 10, 15, 30, 60 })
        {
            var item = new ToolStripMenuItem($"{sec} seconds") { Checked = settings.CaptureIntervalSeconds == sec };
            item.Click += (_, _) =>
            {
                settings.CaptureIntervalSeconds = sec;
                settings.Save();
                foreach (ToolStripMenuItem i in shotMenu.DropDownItems) i.Checked = i == item;
            };
            shotMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(shotMenu);

        var phoneItem = new ToolStripMenuItem("Check test phone over USB") { Checked = settings.DeviceChecks };
        phoneItem.Click += (_, _) =>
        {
            settings.DeviceChecks = !settings.DeviceChecks;
            phoneItem.Checked = settings.DeviceChecks;
            settings.Save();
            _ = CheckNowAsync();
        };
        menu.Items.Add(phoneItem);

        startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        { Checked = IsStartupEnabled() };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        tray.ContextMenuStrip = menu;
    }

    void ApplyInterval()
    {
        timer.Interval = Math.Max(1, settings.IntervalMinutes) * 60_000;
        timer.Start();
        foreach (ToolStripMenuItem i in intervalMenu.DropDownItems)
            i.Checked = (int)i.Tag! == settings.IntervalMinutes;
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsStartupEnabled()) key.DeleteValue(RunValue, false);
        else key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        startupItem.Checked = IsStartupEnabled();
    }

    // ---------------------------------------------------------------- checks

    async Task CheckNowAsync()
    {
        if (checking || browser.IsSignInOpen) return;
        checking = true;
        Refresh();
        try
        {
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
                    snap.Device = await Task.Run(() => DeviceChecker.CheckAsync(settings.AdbPath, pkgs));
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
            lastError = ex.Message;
            Log.Write("Check failed: " + ex);
        }
        finally
        {
            checking = false;
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

    void ShowAccount()
    {
        if (accountOpen) return;
        accountOpen = true;
        try
        {
            var saved = CredentialStore.Load();
            using var form = new AccountForm(saved?.Email, saved != null);
            if (form.ShowDialog() != DialogResult.OK) return;

            if (form.RemoveRequested)
            {
                CredentialStore.Clear();
                Balloon("Twelve Testers", "Saved login removed.");
                return;
            }

            var pw = form.Password.Length > 0 ? form.Password : saved?.Password ?? "";
            CredentialStore.Save(form.Email, pw);
            loginFailed = false;
        }
        finally
        {
            accountOpen = false;
        }
        _ = CheckNowAsync();
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
        balloonAction = null; // callers that want a custom click action set it after this
        tray.BalloonTipTitle = title;
        tray.BalloonTipText = text;
        tray.BalloonTipIcon = ToolTipIcon.Info;
        tray.ShowBalloonTip(8000);
    }

    void Refresh()
    {
        int count = todos.Count(t => t.Priority != Priority.Low);
        bool unknown = last == null || lastError != null;
        SetIcon(count, unknown);

        string tip = checking ? "Twelve Testers — checking…"
            : lastError != null ? "Twelve Testers — last check failed"
            : last == null ? "Twelve Testers"
            : !last.SignedIn ? (last.LoginError != null ? "Twelve Testers — login failed" : "Twelve Testers — login needed")
            : count == 0 ? "Twelve Testers — all done"
            : $"Twelve Testers — {count} to do";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;

        listForm.SetData(last, todos, lastError, checking);
        RebuildFeedbackMenu();
    }

    void RebuildFeedbackMenu()
    {
        feedbackMenu.DropDownItems.Clear();
        var sessions = last is { SignedIn: true } ? last.Sessions : new List<SessionInfo>();
        foreach (var x in sessions)
        {
            var appItem = new ToolStripMenuItem(string.IsNullOrEmpty(x.App) ? x.Partner : x.App);
            if (Snapshot.IsSetupStage(x))
                appItem.DropDownItems.Add(new ToolStripMenuItem("Upload install screenshot (Day 1 check-in)…", null,
                    (_, _) => ShowInstallCheckin(x)) { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) });
            else if (last!.SiteCheckinDue.Contains(x.Id))
                appItem.DropDownItems.Add(new ToolStripMenuItem("Check in on the site (today)…", null,
                    (_, _) => ShowCheckin(x, daily: true)) { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) });
            appItem.DropDownItems.Add("Write report with screenshots…", null, (_, _) => _ = ShowFeedbackAsync(x));
            appItem.DropDownItems.Add("Open report page in browser", null, (_, _) => OpenUrl(x.FeedbackUrl));
            var play = new ToolStripMenuItem("Google Play tester feedback (opens on phone)") { Enabled = x.Package != null };
            play.Click += async (_, _) =>
            {
                var err = await Task.Run(() => DeviceChecker.OpenPlayPageAsync(settings.AdbPath, x.Package!));
                if (err != null) Balloon("Twelve Testers", err);
                else Balloon("Google Play opened on your phone",
                    "Scroll to the tester section and tap \"Write feedback\" (it's private to the developer).");
            };
            appItem.DropDownItems.Add(play);
            appItem.DropDownItems.Add("Open session page", null, (_, _) => OpenUrl(x.Url));
            feedbackMenu.DropDownItems.Add(appItem);
        }
        feedbackMenu.Enabled = feedbackMenu.DropDownItems.Count > 0;
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

    void ShowReminder(bool auto)
    {
        _ = RefreshPhoneAsync(); // don't show stale "not installed"
        reminderForm.SetData(last, settings.ConfirmedOn(DateTime.Today));
        reminderForm.ShowNearTray();
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

    void AfterTesting(CaptureRun? run)
    {
        if (run == null) return;
        var s = run.Session;
        if (!settings.ConfirmedOn(DateTime.Today).Contains(s.Url))
        {
            var ok = MessageBox.Show(reminderForm,
                $"Testing stopped — {run.Count} screenshot(s) saved.\n\nMark {s.App} as tested today?",
                "Twelve Testers", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ok == DialogResult.Yes)
            {
                settings.Confirm(s.Url);
                Log.Write($"Confirmed tested today: {s.App}");
                reminderForm.SetData(last, settings.ConfirmedOn(DateTime.Today));
                RebuildTodos();
            }
        }
    }

    async Task ShowFeedbackAsync(SessionInfo x)
    {
        string deviceLine = "Device: ";
        try
        {
            var model = last?.Device?.Model;
            var android = await Task.Run(() => DeviceChecker.AndroidVersionAsync(settings.AdbPath));
            var ver = x.Package == null ? null : await Task.Run(() => DeviceChecker.AppVersionAsync(settings.AdbPath, x.Package));
            deviceLine += string.Join(", ", new[]
            {
                model, android == null ? null : $"Android {android}", ver == null ? null : $"app version {ver}"
            }.Where(v => !string.IsNullOrEmpty(v)));
        }
        catch { /* phone info is a nice-to-have */ }

        var form = new FeedbackForm(x, capture, deviceLine, d => browser.SubmitFeedbackAsync(x, d));
        form.Show();
        form.Activate();
    }

    // ------------------------------------------------- install check-in & phone

    void ShowInstallCheckin(SessionInfo x) => ShowCheckin(x, daily: false);

    void ShowCheckin(SessionInfo x, bool daily)
    {
        var key = (daily ? "daily:" : "install:") + x.Url;
        if (checkinForms.TryGetValue(key, out var open) && !open.IsDisposed)
        {
            open.Activate();
            return;
        }
        bool installed = last?.Device?.IsInstalled(x.Package) == true;
        var form = new InstallCheckinForm(x, browser, () => settings.AdbPath, installed, daily);
        form.CheckedIn += s =>
        {
            settings.Confirm(s.Url); // the screenshot proves it was opened today
            if (last != null) { last.SiteCheckinDue.Remove(s.Id); last.SiteCheckinDone.Add(s.Id); }
            RebuildTodos();
            if (reminderForm.Visible) reminderForm.SetData(last, settings.ConfirmedOn(DateTime.Today));
            Balloon(daily ? $"{s.App}: checked in on the site ✓" : $"{s.App}: Day 1 check-in done ✓", "Refreshing your list…");
            _ = CheckNowAsync();
        };
        form.FormClosed += (_, _) => checkinForms.Remove(key);
        checkinForms[key] = form;
        form.Show();
        form.Activate();
    }

    /// <summary>
    /// Re-reads which apps are installed on the phone (cheap adb call) without re-reading the site,
    /// and offers the install check-in as soon as a setup-stage app appears.
    /// </summary>
    async Task RefreshPhoneAsync()
    {
        if (phoneRefreshing || checking || !settings.DeviceChecks || last is not { SignedIn: true }) return;
        phoneRefreshing = true;
        try
        {
            var snap = last;
            var installed = await Task.Run(() => DeviceChecker.InstalledPackagesAsync(settings.AdbPath));
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
            if (reminderForm.Visible) reminderForm.SetData(last, settings.ConfirmedOn(DateTime.Today));

            var setup = newlyInstalled.Where(Snapshot.IsSetupStage).ToList();
            if (setup.Count > 0)
            {
                var x = setup[0];
                Balloon($"{x.App} is installed",
                    "Click here to upload the install screenshot (Day 1 check-in)." +
                    (setup.Count > 1 ? $" {setup.Count - 1} more new install(s) are in the menu." : ""));
                balloonAction = () => ShowInstallCheckin(x);
            }
        }
        catch (Exception ex) { Log.Write("Phone refresh failed: " + ex.Message); }
        finally { phoneRefreshing = false; }
    }

    void ShowList()
    {
        _ = RefreshPhoneAsync();
        listForm.SetData(last, todos, lastError, checking);
        listForm.ShowNearTray();
    }

    public void ShowTaskList() => ShowList();

    void OpenItem(TodoItem t)
    {
        if (string.IsNullOrEmpty(t.Url)) return;
        if (t.Url == "app:test") ShowReminder(auto: false);
        else if (t.Url == "app:account") ShowAccount();
        else if (t.Url == "app:signin") browser.ShowSignIn();
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
        reminderForm.Dispose();
        tray.Visible = false;
        tray.Dispose();
        browser.Dispose();
        listForm.Dispose();
        currentIcon?.Dispose();
    }

    void Exit()
    {
        Stop();
        if (System.Windows.Application.Current.MainWindow is MainWindow window)
            window.AllowClose = true;
        System.Windows.Application.Current.Shutdown();
    }

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

