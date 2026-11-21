using System.Diagnostics;
using System.Drawing.Imaging;

namespace TwelveTestersTray;

/// <summary>
/// Write a Twelve Testers report for one app and attach screenshots captured while testing.
/// Nothing is sent until you press "Submit report" and confirm.
/// </summary>
public class FeedbackForm : Form
{
    const int MaxShots = 4;
    const long MaxBytes = 5 * 1024 * 1024;

    static readonly (string Value, string Text)[] Types =
    {
        ("BUG", "Bug"), ("CRASH", "Crash"), ("UI_UX", "UI/UX issue"), ("PERFORMANCE", "Performance issue"),
        ("FEATURE_SUGGESTION", "Feature suggestion"), ("GENERAL", "General feedback"), ("OTHER", "Other")
    };
    static readonly (string Value, string Text)[] Severities =
        { ("LOW", "Low"), ("MEDIUM", "Medium"), ("HIGH", "High"), ("CRITICAL", "Critical") };

    readonly SessionInfo session;
    readonly CaptureManager capture;
    readonly Func<FeedbackDraft, Task<string?>> submit;

    readonly ComboBox type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    readonly ComboBox severity = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly TextBox title = new() { Dock = DockStyle.Fill, PlaceholderText = "e.g. Score resets after closing the app" };
    readonly TextBox description = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    readonly FlowLayoutPanel shots = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, BackColor = Color.FromArgb(245, 245, 245) };
    readonly Label shotsLabel = new() { AutoSize = true };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 12, 8, 0) };
    readonly Button send = new() { Text = "Submit report", AutoSize = true };

    readonly List<(string File, CheckBox Box)> items = new();

    public FeedbackForm(SessionInfo session, CaptureManager capture, string deviceLine,
                        Func<FeedbackDraft, Task<string?>> submit)
    {
        this.session = session;
        this.capture = capture;
        this.submit = submit;

        Text = $"Feedback — {session.App}";
        Font = new Font("Segoe UI", 9.75f);
        Size = new Size(980, 760);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var t in Types) type.Items.Add(t.Text);
        foreach (var s in Severities) severity.Items.Add(s.Text);
        type.SelectedIndex = 0;
        severity.SelectedIndex = 1;

        description.Text =
            "Steps to reproduce:\r\n1. \r\n2. \r\n\r\nExpected:\r\n\r\nWhat happened:\r\n\r\n" + deviceLine;

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(0, 0, 0, 8),
            Text = $"Sent privately to {session.Partner} through Twelve Testers. One issue per report — you can send more anytime."
        };

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        top.Controls.Add(new Label { Text = "Type", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        top.Controls.Add(type);
        top.Controls.Add(new Label { Text = "Severity", AutoSize = true, Margin = new Padding(16, 6, 4, 0) });
        top.Controls.Add(severity);

        var shotButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var captureNow = new Button { Text = "📷 Capture now from phone", AutoSize = true };
        captureNow.Click += async (_, _) =>
        {
            captureNow.Enabled = false;
            var (file, err) = await capture.CaptureNowAsync(session);
            captureNow.Enabled = true;
            if (err != null) { MessageBox.Show(this, err, Text); return; }
            AddThumb(file!, check: true, atStart: true);
        };
        var addFile = new Button { Text = "Add image…", AutoSize = true };
        addFile.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif", Multiselect = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                foreach (var f in dlg.FileNames) AddThumb(f, check: true, atStart: true);
        };
        var openFolder = new Button { Text = "Open captures folder", AutoSize = true };
        openFolder.Click += (_, _) =>
        {
            var dir = CaptureManager.FolderFor(session);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        };
        shotButtons.Controls.AddRange(new Control[] { shotsLabel, captureNow, addFile, openFolder });
        shotsLabel.Margin = new Padding(0, 6, 12, 0);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 4) };
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // intro
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // type/severity
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // title label
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // title
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // desc label
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 38));  // desc
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // shots header
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 62));  // shots
        grid.Controls.Add(intro);
        grid.Controls.Add(top);
        grid.Controls.Add(new Label { Text = "Title *", AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
        grid.Controls.Add(title);
        grid.Controls.Add(new Label { Text = "Description *", AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
        grid.Controls.Add(description);
        grid.Controls.Add(shotButtons);
        grid.Controls.Add(shots);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 8, 10, 8) };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += (_, _) => Close();
        var inBrowser = new Button { Text = "Open site form instead", AutoSize = true };
        inBrowser.Click += (_, _) => OpenInBrowser();
        send.Font = new Font(Font, FontStyle.Bold);
        send.Click += async (_, _) => await SendAsync();
        bottom.Controls.AddRange(new Control[] { cancel, send, inBrowser, status });

        Controls.Add(grid);
        Controls.Add(bottom);

        foreach (var f in CaptureManager.ShotsFor(session)) AddThumb(f, check: false, atStart: false);
        UpdateCount();
    }

    // ---------------------------------------------------------------- thumbnails

    void AddThumb(string file, bool check, bool atStart)
    {
        Image thumb;
        try
        {
            using var fs = File.OpenRead(file);
            using var img = Image.FromStream(fs);
            int h = 200, w = Math.Max(1, img.Width * h / Math.Max(1, img.Height));
            thumb = new Bitmap(img, new Size(w, h));
        }
        catch { return; }

        var pic = new PictureBox
        {
            Image = thumb, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(thumb.Width, 200), Cursor = Cursors.Hand
        };
        var box = new CheckBox
        {
            Text = File.GetLastWriteTime(file).ToString("dd MMM HH:mm:ss"), AutoSize = true
        };
        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(6), Padding = new Padding(4),
            BackColor = Color.White
        };
        pic.Click += (_, _) => box.Checked = !box.Checked;
        pic.DoubleClick += (_, _) => { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } };
        box.CheckedChanged += (_, _) =>
        {
            if (box.Checked && items.Count(i => i.Box.Checked) > MaxShots)
            {
                box.Checked = false;
                MessageBox.Show(this, $"You can attach up to {MaxShots} screenshots per report.", Text);
            }
            card.BackColor = box.Checked ? Color.FromArgb(220, 235, 255) : Color.White;
            UpdateCount();
        };
        card.Controls.Add(pic);
        card.Controls.Add(box);
        shots.Controls.Add(card);
        if (atStart) shots.Controls.SetChildIndex(card, 0);
        items.Add((file, box));
        if (check) box.Checked = items.Count(i => i.Box.Checked) < MaxShots;
        UpdateCount();
    }

    void UpdateCount()
    {
        int n = items.Count(i => i.Box.Checked);
        shotsLabel.Text = items.Count == 0
            ? "Screenshots: none captured yet — use Start testing, or Capture now"
            : $"Screenshots: {n}/{MaxShots} selected  (click to pick, double-click to view)";
    }

    List<string> SelectedFiles() => items.Where(i => i.Box.Checked).Select(i => i.File).ToList();

    // ------------------------------------------------------------------ sending

    async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(title.Text)) { MessageBox.Show(this, "Add a title.", Text); title.Focus(); return; }
        if (string.IsNullOrWhiteSpace(description.Text)) { MessageBox.Show(this, "Add a description.", Text); return; }

        List<string> files;
        try { files = SelectedFiles().Select(FitUnderLimit).ToList(); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't prepare a screenshot: " + ex.Message, Text); return; }

        var ok = MessageBox.Show(this,
            $"Send this report to {session.Partner}?\n\n{Types[type.SelectedIndex].Text} · {Severities[severity.SelectedIndex].Text}\n" +
            $"\"{title.Text.Trim()}\"\n{files.Count} screenshot(s)\n\nReports can't be unsent.",
            "Submit report", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ok != DialogResult.Yes) return;

        var draft = new FeedbackDraft
        {
            Type = Types[type.SelectedIndex].Value,
            Severity = Severities[severity.SelectedIndex].Value,
            Title = title.Text.Trim(),
            Description = description.Text.Trim(),
            Files = files
        };

        UseWaitCursor = true;
        send.Enabled = false;
        status.Text = "Sending… (uploading screenshots can take a little while)";
        string? err;
        try { err = await submit(draft); }
        catch (Exception ex) { err = ex.Message; }
        UseWaitCursor = false;
        send.Enabled = true;

        if (err == null)
        {
            status.Text = "";
            MessageBox.Show(this, $"Report sent to {session.Partner} ✓", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        else
        {
            status.Text = "Not sent.";
            MessageBox.Show(this, err + "\n\nYou can try again, or use \"Open site form instead\".", "Report not sent",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>The site allows 5 MB per screenshot: re-encode bigger ones as JPEG.</summary>
    static string FitUnderLimit(string file)
    {
        if (new FileInfo(file).Length <= MaxBytes) return file;
        var dir = Path.Combine(Path.GetTempPath(), "TwelveTestersTray");
        Directory.CreateDirectory(dir);
        var outFile = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".jpg");
        using var img = Image.FromFile(file);
        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        img.Save(outFile, enc, p);
        return outFile;
    }

    /// <summary>Fallback: open the site's form and a folder with the chosen screenshots to drag in.</summary>
    void OpenInBrowser()
    {
        var files = SelectedFiles();
        if (files.Count > 0)
        {
            var dir = Path.Combine(CaptureManager.FolderFor(session), "to-upload");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            foreach (var f in files) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        TrayContext.OpenUrl(session.FeedbackUrl);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var c in shots.Controls.OfType<FlowLayoutPanel>())
            foreach (var p in c.Controls.OfType<PictureBox>()) p.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
