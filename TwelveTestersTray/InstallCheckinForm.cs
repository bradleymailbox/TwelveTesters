namespace TwelveTestersTray;

/// <summary>
/// Day 1 "I installed it" check-in for one swap app: opens the app on the phone, takes a fresh
/// screenshot (clock visible), uploads it to the session page and shows the site's verdict.
/// Nothing is final until you press "Confirm check-in".
/// </summary>
public class InstallCheckinForm : Form
{
    readonly SessionInfo session;
    readonly BrowserForm browser;
    readonly Func<string?> adbPath;
    /// <summary>Daily check-in (Check-ins page, 10-minute timer) instead of the Day 1 install step.</summary>
    readonly bool daily;

    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(240, 240, 240) };
    readonly Label shotInfo = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 0) };
    readonly CheckBox pressJoined = new() { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
    readonly Label verdict = new() { AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(0, 6, 0, 4) };
    readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 12, 8, 0) };

    readonly Button capture = new() { Text = "📱 Open app on phone && capture", AutoSize = true };
    readonly Button pick = new() { Text = "Use an image file…", AutoSize = true };
    readonly Button upload = new() { Text = "Upload && check", AutoSize = true, Enabled = false };
    readonly Button confirm = new() { Text = "✓ Confirm check-in", AutoSize = true, Visible = false };
    readonly Button retry = new() { Text = "Try another screenshot", AutoSize = true, Visible = false };

    string? file;
    DateTime capturedAt;
    bool busy;

    /// <summary>Raised after the site shows "Done today".</summary>
    public event Action<SessionInfo>? CheckedIn;

    public InstallCheckinForm(SessionInfo session, BrowserForm browser, Func<string?> adbPath, bool installedOnPhone,
                              bool daily = false)
    {
        this.session = session;
        this.browser = browser;
        this.adbPath = adbPath;
        this.daily = daily;

        Text = daily ? $"Daily check-in — {session.App}" : $"Install check-in — {session.App}";
        Font = new Font("Segoe UI", 9.75f);
        Size = new Size(900, 780);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(840, 0),
            Text = $"Swap with {session.Partner}. Day 1 needs a fresh screenshot of {session.App} open on your phone, " +
                   "with the clock visible. This opens the app, captures it, uploads it to the session page and shows " +
                   "you the site's check. Nothing is final until you press Confirm check-in."
        };
        pressJoined.Text = "Also press \"I joined the test\" if the site still asks (only tick this if you really joined)";
        pressJoined.Checked = installedOnPhone; // installed from Play = you're in the closed test

        var shotButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 8, 0, 4) };
        capture.Font = new Font(Font, FontStyle.Bold);
        capture.Enabled = session.Package != null;
        capture.Click += async (_, _) => { if (this.daily) await DailyAsync(); else await CaptureAsync(); };
        pick.Click += (_, _) => PickFile();
        shotButtons.Controls.AddRange(new Control[] { capture, pick, shotInfo });

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(verdict);
        right.Controls.Add(details);

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        split.Controls.Add(preview, 0, 0);
        split.Controls.Add(right, 1, 0);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 4) };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(intro);
        grid.Controls.Add(pressJoined);
        grid.Controls.Add(shotButtons);
        grid.Controls.Add(split);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8) };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        var page = new Button { Text = "Open session page", AutoSize = true };
        page.Click += (_, _) => TrayContext.OpenUrl(session.Url);
        upload.Font = new Font(Font, FontStyle.Bold);
        upload.Click += async (_, _) => await UploadAsync();
        confirm.Font = new Font(Font, FontStyle.Bold);
        confirm.ForeColor = Color.FromArgb(30, 120, 50);
        confirm.Click += async (_, _) => await ConfirmAsync();
        retry.Click += (_, _) => Reset("Take or pick another screenshot, then Upload & check again.");
        bottom.Controls.AddRange(new Control[] { close, confirm, upload, retry, page, status });

        Controls.Add(grid);
        Controls.Add(bottom);

        if (daily)
        {
            intro.Text = $"Swap with {session.Partner}. Use {session.App} on your phone for a few minutes first. " +
                         "Start check-in then presses \"All good\" on the site (this starts its 10-minute timer), opens the app " +
                         "on your phone, captures it with the clock visible and uploads it. You see the site's check and press " +
                         "Confirm check-in to finish.";
            capture.Text = "📱 Start check-in (open app, capture && upload)";
            pressJoined.Visible = false;
            pick.Visible = false;
            upload.Visible = false;
            verdict.Text = "Ready to check in";
        }
        else verdict.Text = "Step 1: capture a screenshot";
        details.Text = session.Package == null
            ? "The app's package name isn't known yet, so it can't be opened automatically — run Check now, or use an image file."
            : $"Package: {session.Package}\r\nMake sure the phone is unlocked and connected by USB.";
    }

    // ------------------------------------------------------------- screenshot

    async Task CaptureAsync()
    {
        if (busy) return;
        SetBusy(true, "Opening the app on your phone…");
        try
        {
            var (png, err) = await Task.Run(() => DeviceChecker.CaptureAppAsync(adbPath(), session.Package!));
            if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            var path = SaveShot(png!, "install");
            SetShot(path, capturedAt);
        }
        catch (Exception ex) { MessageBox.Show(this, "Capture failed: " + ex.Message, Text); }
        finally { SetBusy(false, ""); }
    }

    void PickFile()
    {
        using var dlg = new OpenFileDialog { Filter = "Screenshots|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var when = File.GetLastWriteTime(dlg.FileName);
        if (DateTime.Now - when > TimeSpan.FromMinutes(10) &&
            MessageBox.Show(this,
                $"That file is from {when:dd MMM HH:mm}. The site checks that the screenshot is fresh and its clock matches " +
                "the check-in time, so an old one will likely be flagged.\n\nUse it anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        SetShot(dlg.FileName, when);
    }

    void SetShot(string path, DateTime when)
    {
        file = path;
        capturedAt = when;
        var old = preview.Image;
        using (var fs = File.OpenRead(path)) preview.Image = new Bitmap(Image.FromStream(fs));
        old?.Dispose();
        shotInfo.Text = $"Screenshot from {when:HH:mm:ss} — check the clock is visible";
        verdict.ForeColor = SystemColors.ControlText;
        if (daily)
        {
            verdict.Text = "Uploading…";
            details.Text = "Waiting for the site's screenshot check.";
            return;
        }
        verdict.Text = "Step 2: upload it";
        details.Text = "Press Upload & check. The site compares the clock on the screenshot with the time now, " +
                       "so upload within a few minutes of capturing.";
        upload.Enabled = true;
    }

    // ---------------------------------------------------------------- upload

    async Task UploadAsync()
    {
        if (busy || file == null) return;
        if (DateTime.Now - capturedAt > TimeSpan.FromMinutes(5) &&
            MessageBox.Show(this, $"This screenshot is {(int)(DateTime.Now - capturedAt).TotalMinutes} minutes old — the clock check may fail. " +
                                  "Capture a new one first?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            await CaptureAsync();
            if (file == null) return;
        }

        SetBusy(true, "Uploading and waiting for the site's check…");
        InstallCheckResult res;
        try { res = await browser.BeginInstallCheckinAsync(session, file, capturedAt, pressJoined.Checked); }
        catch (Exception ex) { res = new InstallCheckResult { Message = ex.Message }; }
        finally { SetBusy(false, ""); }

        if (res.Stage == "join")
        {
            if (MessageBox.Show(this,
                    $"The site still shows \"I joined the test\" for {session.App}.\n\nHave you joined the Google Group and opted in? " +
                    "If yes, I'll press it and continue.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                pressJoined.Checked = true;
                await UploadAsync();
            }
            return;
        }

        ShowResult(res);
    }

    // ------------------------------------------------------------- daily check-in

    async Task DailyAsync()
    {
        if (busy || session.Package == null) return;
        SetBusy(true, "Starting the check-in on the site…");
        InstallCheckResult res;
        try
        {
            res = await browser.BeginDailyCheckinAsync(session, async () =>
            {
                status.Text = "Timer started — opening the app on your phone and capturing…";
                var (png, err) = await Task.Run(() => DeviceChecker.CaptureAppAsync(adbPath(), session.Package!));
                if (err != null) return (null, DateTime.Now, err);
                var path = SaveShot(png!, "checkin");
                SetShot(path, capturedAt);
                status.Text = "Uploading and waiting for the site's check…";
                return (path, capturedAt, null);
            });
        }
        catch (Exception ex) { res = new InstallCheckResult { Message = ex.Message }; }
        finally { SetBusy(false, ""); }
        ShowResult(res);
    }

    string SaveShot(byte[] png, string prefix)
    {
        var dir = Path.Combine(CaptureManager.FolderFor(session), prefix);
        Directory.CreateDirectory(dir);
        capturedAt = DateTime.Now;
        var path = Path.Combine(dir, $"{prefix}-{capturedAt:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    void ShowResult(InstallCheckResult res)
    {
        if (res.Stage == "done")
        {
            verdict.Text = daily ? "Checked in on the site today ✓" : "Done today ✓";
            verdict.ForeColor = Color.FromArgb(30, 120, 50);
            details.Text = (res.Message is "" or "checked" or "saved" ? "" : res.Message + "\r\n\r\n") + res.Details.Replace("\n", "\r\n");
            confirm.Visible = retry.Visible = upload.Visible = false;
            capture.Enabled = false;
            status.Text = "";
            CheckedIn?.Invoke(session);
            return;
        }

        verdict.Text = res.CanConfirm
            ? (string.IsNullOrEmpty(res.Confidence) ? "Check passed" : res.Confidence)
            : (string.IsNullOrEmpty(res.Confidence) ? "Not accepted" : res.Confidence + " — not accepted");
        verdict.ForeColor = res.CanConfirm && res.Confidence.StartsWith("High", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(30, 120, 50)
            : res.CanConfirm ? Color.FromArgb(190, 120, 0) : Color.FromArgb(180, 40, 40);
        details.Text = (res.CanConfirm ? "" : res.Message + "\r\n\r\n") + res.Details.Replace("\n", "\r\n");

        upload.Visible = !daily && !res.CanConfirm;
        confirm.Visible = res.CanConfirm;
        retry.Visible = res.CanConfirm || !string.IsNullOrEmpty(res.Details);
        status.Text = res.CanConfirm ? "Review the result, then confirm (or try another screenshot)." : "Not uploaded.";
        if (res.CanConfirm) confirm.Focus();
    }

    async Task ConfirmAsync()
    {
        if (busy) return;
        SetBusy(true, "Confirming…");
        string? err;
        try { err = await browser.ConfirmInstallCheckinAsync(); }
        finally { SetBusy(false, ""); }

        confirm.Visible = false;
        if (err == null)
        {
            verdict.Text = daily ? "Checked in on the site today ✓" : "Done today ✓";
            verdict.ForeColor = Color.FromArgb(30, 120, 50);
            status.Text = daily ? "Daily check-in saved." : "Day 1 check-in saved.";
            retry.Visible = false;
            upload.Visible = false;
            capture.Enabled = false;
            CheckedIn?.Invoke(session);
        }
        else
        {
            status.Text = "Not confirmed.";
            MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            upload.Visible = !daily;
        }
    }

    void Reset(string msg)
    {
        browser.CancelInstallCheckin();
        confirm.Visible = false;
        retry.Visible = false;
        upload.Visible = !daily;
        upload.Enabled = false;
        file = null;
        verdict.Text = daily ? "Ready to check in" : "Step 1: capture a screenshot";
        if (daily) msg = "Press Start check-in again (it's fine within the site's 10-minute window).";
        verdict.ForeColor = SystemColors.ControlText;
        details.Text = msg;
        status.Text = "";
    }

    void SetBusy(bool on, string text)
    {
        busy = on;
        UseWaitCursor = on;
        capture.Enabled = !on && session.Package != null;
        pick.Enabled = upload.Enabled = confirm.Enabled = retry.Enabled = !on;
        if (!on && file == null) upload.Enabled = false;
        status.Text = text;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy) { e.Cancel = true; return; } // let the upload finish first
        browser.CancelInstallCheckin();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        preview.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
