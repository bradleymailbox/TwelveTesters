namespace TwelveTestersTray;

/// <summary>
/// "Time to test your swap apps" window: one row per app you're testing, with
/// "Open on phone" and an explicit "I tested it today" confirmation.
/// </summary>
public class ReminderForm : Form
{
    readonly TableLayoutPanel rows = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 5, Padding = new Padding(12, 4, 12, 4)
    };
    readonly Label header = new() { Dock = DockStyle.Top, Height = 58, Padding = new Padding(12, 10, 12, 0) };

    Snapshot? snap;
    HashSet<string> confirmed = new();
    readonly CaptureManager capture;
    readonly Dictionary<string, Button> testButtons = new();

    /// <summary>Raised with the session URL when you confirm (true) or undo (false).</summary>
    public event Action<SessionInfo, bool>? ConfirmChanged;
    public event Action<SessionInfo>? StartTesting;
    public event Action<SessionInfo>? StopTesting;
    public event Action<SessionInfo>? InstallRequested;
    public event Action<SessionInfo>? FeedbackRequested;
    public event Action<SessionInfo>? InstallCheckinRequested;
    /// <summary>Daily check-in on the site (screenshot upload).</summary>
    public event Action<SessionInfo>? CheckinRequested;
    public event Action? SnoozeClicked;

    public ReminderForm(CaptureManager capture)
    {
        this.capture = capture;
        capture.Changed += _ => UpdateTestButtons();
        Text = "Twelve Testers — time to test";
        Font = new Font("Segoe UI", 9.75f);
        Size = new Size(1060, 380);
        MinimumSize = new Size(620, 240);
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = true;

        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8)
        };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Hide();
        var snooze = new Button { Text = "Remind me in 30 min", AutoSize = true };
        snooze.Click += (_, _) => { SnoozeClicked?.Invoke(); Hide(); };
        bottom.Controls.AddRange(new Control[] { close, snooze });

        Controls.Add(rows);
        Controls.Add(header);
        Controls.Add(bottom);
    }

    public void SetData(Snapshot? s, HashSet<string> confirmedToday)
    {
        snap = s;
        confirmed = confirmedToday;
        Rebuild();
    }

    void Rebuild()
    {
        rows.SuspendLayout();
        rows.Controls.Clear();
        rows.RowStyles.Clear();
        rows.RowCount = 0;
        testButtons.Clear();

        var daily = snap?.DailySessions.ToList() ?? new();
        var all = snap?.ActiveSessions.ToList() ?? new();
        // Still due on the site = not done, even if you pressed "I tested it today" earlier.
        bool IsDone(SessionInfo x) => confirmed.Contains(x.Url) && snap?.SiteCheckinDue.Contains(x.Id) != true;
        int done = daily.Count(IsDone);
        int setup = all.Count(Snapshot.IsSetupStage);

        header.Text = all.Count == 0
            ? "No active swaps right now."
            : (daily.Count > 0 && done == daily.Count
                  ? $"All {daily.Count} daily apps confirmed for today. Nice work ✓"
                  : $"Use each app on your phone for a few minutes, then check in on the site. " +
                    $"{done} of {daily.Count} daily apps done today.")
              + (setup > 0 ? $"\n{setup} new swap{(setup == 1 ? " is" : "s are")} still in setup on the site (join / install + screenshot)." : "");

        // Daily apps first, then ones still in setup.
        foreach (var x in all.OrderBy(Snapshot.IsSetupStage))
        {
            bool isDone = IsDone(x);
            bool inSetup = Snapshot.IsSetupStage(x);
            var dev = snap!.Device;
            bool connected = dev is { Connected: true };
            bool installed = connected && x.Package != null && dev!.Installed.Contains(x.Package);

            string phoneInfo;
            if (!connected) phoneInfo = "Phone not connected";
            else if (x.Package == null) phoneInfo = "Package unknown";
            else if (!installed) phoneInfo = "⚠ Not installed on phone";
            else if (dev!.LastOpened.TryGetValue(x.Package, out var t) && t.Date == DateTime.Today)
                phoneInfo = $"Opened on phone at {t:HH:mm}";
            else phoneInfo = "Not opened on phone yet today";
            if (inSetup)
                phoneInfo += x.MyStatus.Equals("Get started", StringComparison.OrdinalIgnoreCase)
                    ? (installed ? "\nSetup: installed — upload the install screenshot" : "\nSetup: join the test on the site")
                    : "\nSetup: upload the install screenshot";

            var name = new Label
            {
                Text = (isDone ? "✓ " : "") + x.App + (inSetup ? "  (new)" : "") + $"\n{x.Partner} · {x.DaysLeft}",
                AutoSize = true, Margin = new Padding(0, 8, 8, 8),
                ForeColor = isDone ? Color.FromArgb(40, 140, 60) : SystemColors.ControlText
            };
            var info = new Label { Text = phoneInfo, AutoSize = true, Margin = new Padding(0, 8, 8, 8), ForeColor = Color.DimGray };

            Button open;
            if (connected && x.Package != null && !installed)
            {
                // Not on the phone yet: offer to open its Play page there instead.
                open = new Button { Text = "Install on phone", AutoSize = true };
                open.Click += (_, _) => InstallRequested?.Invoke(x);
            }
            else
            {
                open = new Button { AutoSize = true, Enabled = installed };
                open.Click += (_, _) =>
                {
                    if (capture.IsRunning(x)) StopTesting?.Invoke(x);
                    else StartTesting?.Invoke(x);
                };
                testButtons[x.Url] = open;
                new ToolTip().SetToolTip(open,
                    "Opens the app on your phone and screenshots it every few seconds while it's on screen,\n" +
                    "so you can attach them to feedback. Press again to stop.");
            }

            bool siteDue = !inSetup && snap.SiteCheckinDue.Contains(x.Id);
            bool siteDone = !inSetup && snap.SiteCheckinDone.Contains(x.Id);

            var confirm = new Button { Text = isDone ? "Undo" : "I tested it today", AutoSize = true };
            if (!isDone) confirm.Font = new Font(Font, FontStyle.Bold);
            if (siteDue)
            {
                // The real thing: check in on the site (this also marks it tested here).
                confirm.Text = "📷 Check in on site";
                confirm.Font = new Font(Font, FontStyle.Bold);
                confirm.Enabled = installed || !connected;
                new ToolTip().SetToolTip(confirm,
                    "Starts the site's 10-minute check-in, opens the app on your phone, captures it and uploads it.\n" +
                    "You see the site's check and press Confirm check-in to finish.");
            }
            else if (siteDone)
            {
                confirm.Text = "✓ Checked in on site";
                confirm.Enabled = false;
            }
            confirm.Click += (_, _) =>
            {
                if (siteDue) { CheckinRequested?.Invoke(x); return; }
                if (isDone)
                {
                    confirmed.Remove(x.Url);
                    ConfirmChanged?.Invoke(x, false);
                }
                else
                {
                    var ok = MessageBox.Show(this,
                        $"Confirm you opened and actually used {x.App} today?",
                        "Confirm testing", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (ok != DialogResult.Yes) return;
                    confirmed.Add(x.Url);
                    ConfirmChanged?.Invoke(x, true);
                }
                Rebuild();
            };

            Button feedback;
            if (inSetup)
            {
                // Before Day 1 there's nothing to give feedback on yet; the next step is the install check-in.
                feedback = new Button { Text = "📷 Upload install screenshot", AutoSize = true, Enabled = installed || !connected };
                if (installed) feedback.Font = new Font(Font, FontStyle.Bold);
                feedback.Click += (_, _) => InstallCheckinRequested?.Invoke(x);
                new ToolTip().SetToolTip(feedback,
                    "Opens the app on your phone, takes a fresh screenshot, uploads it to the session page\n" +
                    "and shows the site's check. You confirm the check-in at the end.");
            }
            else
            {
                feedback = new Button { Text = "Give feedback", AutoSize = true };
                feedback.Click += (_, _) => FeedbackRequested?.Invoke(x);
            }

            int r = rows.RowCount++;
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.Controls.Add(name, 0, r);
            rows.Controls.Add(info, 1, r);
            rows.Controls.Add(open, 2, r);
            rows.Controls.Add(feedback, 3, r);
            rows.Controls.Add(confirm, 4, r);
        }

        rows.ResumeLayout();
        UpdateTestButtons();
    }

    void UpdateTestButtons()
    {
        if (IsDisposed) return;
        foreach (var (url, btn) in testButtons)
        {
            var run = capture.Current;
            bool running = run is { Running: true } && run.Session.Url == url;
            btn.Text = running ? $"■ Stop testing ({run!.Count} shot{(run.Count == 1 ? "" : "s")})" : "▶ Start testing";
            btn.ForeColor = running ? Color.FromArgb(190, 40, 40) : SystemColors.ControlText;
        }
    }

    public void ShowNearTray()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (!Visible) Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}

