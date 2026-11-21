using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TwelveTestersTray.Services;
using TwelveTestersTray.ViewModels;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using PasswordBox = System.Windows.Controls.PasswordBox;
using Brush = System.Windows.Media.Brush;

namespace TwelveTestersTray.Views;

public interface IRefreshablePage { void Refresh(); }

internal static class PageUi
{
    public static TextBlock Text(string value, int size = 14) =>
        new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 0, 0, 8) };

    public static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8), MinWidth = 90 };
        button.Click += (_, _) => action();
        return button;
    }

    public static Border Card(UIElement child)
    {
        var border = new Border
        {
            Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12)
        };
        border.SetResourceReference(Border.StyleProperty, "Card");
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return border;
    }

    public static ScrollViewer Scroll(StackPanel content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public static MainWindow Window => (MainWindow)System.Windows.Application.Current.MainWindow;
}

public sealed class TodayView : UserControl, IRefreshablePage
{
    private readonly TrayContext app;
    private readonly TodayViewModel model;
    private readonly StackPanel body = new();

    public TodayView(TrayContext app)
    {
        this.app = app;
        model = new TodayViewModel(app);
        DataContext = model;
        Content = PageUi.Scroll(body);
        Refresh();
    }

    public void Refresh()
    {
        model.Refresh();
        body.Children.Clear();
        if (app.Error != null)
        {
            var error = new StackPanel();
            error.Children.Add(PageUi.Text(app.BrowserRuntimeMissing ? "WebView2 Runtime is missing" : "Couldn't refresh your tasks", 18));
            error.Children.Add(PageUi.Text(app.Error));
            if (app.BrowserRuntimeMissing)
                error.Children.Add(PageUi.Button("Download WebView2 Runtime", () => TrayContext.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/")));
            var retry = PageUi.Button("Retry", () => _ = app.CheckNowAsync());
            retry.IsEnabled = !app.IsChecking && !app.IsStarting;
            error.Children.Add(retry);
            body.Children.Add(PageUi.Card(error));
            if (app.Snapshot == null) return;
            body.Children.Add(PageUi.Text("Showing the last available results."));
        }
        if (app.Snapshot == null)
        {
            body.Children.Add(PageUi.Text("Loading today's tasks…", 19));
            for (var i = 0; i < 3; i++)
            {
                var placeholder = new StackPanel();
                placeholder.Children.Add(PageUi.Text("Loading app and partner…"));
                placeholder.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 6 });
                body.Children.Add(PageUi.Card(placeholder));
            }
            return;
        }
        if (app.Snapshot is not { SignedIn: true } snap)
        {
            var login = new StackPanel();
            login.Children.Add(PageUi.Text(app.Snapshot.LoginError != null ? "Saved login failed" : "Sign in to see today's swaps and tasks.", 18));
            if (app.Snapshot.LoginError != null)
                login.Children.Add(PageUi.Text(app.Snapshot.LoginError + "\nAutomatic login retries are paused. Update your login or sign in manually."));
            login.Children.Add(PageUi.Button("Fix login", () => PageUi.Window.Navigate("Account")));
            body.Children.Add(PageUi.Card(login));
            return;
        }

        if (app.Settings.DeviceChecks && snap.Device is not { Connected: true })
        {
            var phone = new StackPanel();
            phone.Children.Add(PageUi.Text(snap.Device is { AdbFound: false } ? "ADB not found" : "No phone connected", 18));
            phone.Children.Add(PageUi.Text(snap.Device?.Problem ?? "Connect and unlock your phone, enable USB debugging, and accept the USB authorization prompt."));
            phone.Children.Add(PageUi.Text("ADB path: " + (app.Adb.AdbPath ?? app.Settings.AdbPath ?? "Not configured")));
            phone.Children.Add(PageUi.Button("Configure ADB", () => PageUi.Window.Navigate("Settings")));
            var retry = PageUi.Button("Retry phone check", () => _ = app.CheckNowAsync());
            retry.IsEnabled = !app.IsChecking;
            phone.Children.Add(retry);
            body.Children.Add(PageUi.Card(phone));
        }

        body.Children.Add(PageUi.Text("To do", 19));
        if (app.Error == null && !app.Todos.Any(t => t.Priority != Priority.Low))
            body.Children.Add(PageUi.Card(PageUi.Text("All done for today ✓")));
        foreach (var todo in app.Todos)
        {
            var row = new StackPanel();
            row.Children.Add(PageUi.Text(todo.Title, 16));
            row.Children.Add(PageUi.Text(todo.Detail));
            row.Children.Add(PageUi.Button("Open", () => app.OpenItem(todo)));
            body.Children.Add(PageUi.Card(row));
        }

        body.Children.Add(PageUi.Text("Swaps", 19));
        foreach (var session in snap.ActiveSessions)
        {
            var row = new StackPanel();
            row.Children.Add(PageUi.Text(session.App, 18));
            row.Children.Add(PageUi.Text($"Partner: {session.Partner}  ·  {session.DaysLeft} days left  ·  {session.MyStatus}"));
            var state = SessionCardState.From(session, snap);
            var chipText = PageUi.Text(state.Stage);
            chipText.Margin = new Thickness(0);
            chipText.SetResourceReference(TextBlock.ForegroundProperty, state.BrushKey);
            var chip = new Border { Child = chipText, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            chip.SetResourceReference(Border.StyleProperty, "Chip");
            row.Children.Add(chip);
            row.Children.Add(PageUi.Text($"Installed {state.Installed}  ·  Opened today {state.Opened}  ·  Check-in {state.Checkin}"));
            AddTodayThumbnail(row, session);
            var actions = new WrapPanel();
            var packageKnown = !string.IsNullOrWhiteSpace(session.Package);
            if (!packageKnown) row.Children.Add(PageUi.Text("Package unknown — run Check now or open the session page to finish setup."));
            var install = PageUi.Button("Install on phone", () => _ = InstallAsync(session));
            var start = PageUi.Button("Start testing", () => _ = StartAsync(session));
            install.IsEnabled = start.IsEnabled = packageKnown;
            install.ToolTip = start.ToolTip = packageKnown ? null : "The app's package name is not known yet.";
            actions.Children.Add(install);
            actions.Children.Add(start);
            var stop = PageUi.Button("Stop", () => app.Capture.Stop());
            stop.IsEnabled = app.Capture.IsRunning(session);
            actions.Children.Add(stop);
            actions.Children.Add(PageUi.Button("Check in", () => PageUi.Window.Navigate("Checkin", session)));
            actions.Children.Add(PageUi.Button("Mark tested", () => { app.Settings.Confirm(session.Url); app.RefreshFromSettings(); }));
            actions.Children.Add(PageUi.Button("Report", () => PageUi.Window.Navigate("Report", session)));
            var more = PageUi.Button("⋯", () => { });
            var menu = new ContextMenu();
            var sessionLink = new MenuItem { Header = "Open session page" };
            sessionLink.Click += (_, _) => TrayContext.OpenUrl(session.Url);
            menu.Items.Add(sessionLink);
            var unmark = new MenuItem { Header = "Unmark tested" };
            unmark.Click += (_, _) => { app.Settings.Unconfirm(session.Url); app.RefreshFromSettings(); };
            menu.Items.Add(unmark);
            more.Click += (_, _) => menu.IsOpen = true;
            actions.Children.Add(more);
            row.Children.Add(actions);
            var card = PageUi.Card(row);
            card.SetResourceReference(Border.BorderBrushProperty, state.BrushKey);
            body.Children.Add(card);
        }
    }

    private static void AddTodayThumbnail(StackPanel row, SessionInfo session)
    {
        try
        {
            var path = CaptureManager.ShotsFor(session).FirstOrDefault(p => File.GetLastWriteTime(p).Date == DateTime.Today);
            if (path == null) return;
            var source = new ImagePathConverter().Convert(path, typeof(ImageSource), "180", System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
            if (source == null) return;
            row.Children.Add(new Image { Source = source, Height = 140, Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, ToolTip = "Today's latest capture" });
            row.Children.Add(PageUi.Text($"Latest capture today · {File.GetLastWriteTime(path):HH:mm}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException)
        { /* A missing or incomplete image must not prevent the app card from rendering. */ }
    }

    private async Task StartAsync(SessionInfo session)
    {
        var error = await app.Capture.StartAsync(session);
        if (error != null) app.ShowNotice(error);
        else PageUi.Window.ShowFromTray("Testing");
    }

    private async Task InstallAsync(SessionInfo session)
    {
        var error = await app.Adb.OpenPlayPageAsync(session.Package!);
        if (error != null) app.ShowNotice(error);
        else app.ShowNotice($"Opened {session.App} in Play Store on your phone.");
    }
}

public sealed class TestingView : UserControl, IRefreshablePage
{
    private readonly TrayContext app;
    private readonly TestingViewModel model;
    private readonly StackPanel body = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };

    public TestingView(TrayContext app)
    {
        this.app = app;
        model = new TestingViewModel(app);
        DataContext = model;
        Content = PageUi.Scroll(body);
        ticker.Tick += (_, _) => { if (IsVisible) Refresh(); };
        ticker.Start();
        app.Capture.Changed += _ => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        model.Refresh();
        body.Children.Clear();
        var run = model.Run;
        if (run == null) { body.Children.Add(PageUi.Card(PageUi.Text("Choose an app on Today to start testing."))); return; }
        var elapsed = (run.Ended ?? DateTime.Now) - run.Started;
        body.Children.Add(PageUi.Text(run.Session.App, 20));
        body.Children.Add(PageUi.Card(PageUi.Text($"{(run.Running ? "Testing" : "Stopped")}  ·  {elapsed:mm\\:ss} / 45:00  ·  {run.Count} screenshot(s)")));
        var files = CaptureManager.ShotsFor(run.Session);
        if (files.Count > 0)
        {
            var image = new Image { Source = new ImagePathConverter().Convert(files[0], typeof(ImageSource), "300", System.Globalization.CultureInfo.CurrentCulture) as ImageSource,
                Height = 240, Stretch = Stretch.Uniform, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            body.Children.Add(PageUi.Card(image));
        }
        if (run.Running) body.Children.Add(PageUi.Button("Stop testing", () => { app.Capture.Stop(); Refresh(); }));
        else
        {
            body.Children.Add(PageUi.Button("Mark tested", () => { app.Settings.Confirm(run.Session.Url); app.RefreshFromSettings(); }));
            body.Children.Add(PageUi.Button("Write report", () => PageUi.Window.Navigate("Report", run.Session)));
            body.Children.Add(PageUi.Button("Check in", () => PageUi.Window.Navigate("Checkin", run.Session)));
            body.Children.Add(PageUi.Button("Done", () => PageUi.Window.Navigate("Today")));
            if (run.Problem != null) body.Children.Add(PageUi.Text(run.Problem));
        }
    }
}

public sealed class ReportView : UserControl, IRefreshablePage
{
    private readonly TrayContext app;
    private readonly ReportViewModel model;
    private readonly ComboBox sessions = new() { DisplayMemberPath = "App", MinWidth = 240, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel body = new();
    private readonly ComboBox type = new() { ItemsSource = new[] { "BUG", "CRASH", "UI_UX", "PERFORMANCE", "FEATURE_SUGGESTION", "GENERAL", "OTHER" }, SelectedIndex = 0, MinWidth = 180 };
    private readonly ComboBox severity = new() { ItemsSource = new[] { "LOW", "MEDIUM", "HIGH", "CRITICAL" }, SelectedIndex = 1, MinWidth = 130 };
    private readonly TextBox title = new() { MinWidth = 420 };
    private readonly TextBox description = new() { MinHeight = 150, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel screenshots = new();
    private readonly TextBlock status = PageUi.Text("");
    private readonly TextBlock deviceLine = PageUi.Text("");
    private readonly List<(string Path, CheckBox Box)> files = new();
    private Snapshot? loadedSnapshot;
    private readonly Button confirmSend;

    public ReportView(TrayContext app)
    {
        this.app = app;
        model = new ReportViewModel(app);
        DataContext = model;
        Content = PageUi.Scroll(body);
        sessions.SelectionChanged += (_, _) => { model.Session = sessions.SelectedItem as SessionInfo; LoadScreenshots(); };
        body.Children.Add(PageUi.Text("Choose an app to write a report for."));
        body.Children.Add(sessions);
        var fields = new WrapPanel();
        fields.Children.Add(type);
        fields.Children.Add(severity);
        body.Children.Add(fields);
        body.Children.Add(PageUi.Text("Title"));
        body.Children.Add(title);
        body.Children.Add(PageUi.Text("Description"));
        body.Children.Add(description);
        body.Children.Add(deviceLine);
        body.Children.Add(PageUi.Text("Screenshots (up to 4)"));
        body.Children.Add(screenshots);
        body.Children.Add(PageUi.Button("Add file", AddFile));
        body.Children.Add(PageUi.Button("Review and submit", ReviewSubmit));
        confirmSend = PageUi.Button("Confirm submission", () => _ = SubmitAsync());
        confirmSend.Visibility = Visibility.Collapsed;
        body.Children.Add(confirmSend);
        body.Children.Add(PageUi.Button("Open site form instead", () => { if (model.Session != null) TrayContext.OpenUrl(model.Session.FeedbackUrl); }));
        body.Children.Add(status);
        Refresh();
    }

    public void Select(SessionInfo session) { Refresh(); sessions.SelectedItem = session; }
    public void Refresh()
    {
        if (!ReferenceEquals(loadedSnapshot, app.Snapshot))
        {
            loadedSnapshot = app.Snapshot;
            var selected = model.Session?.Url;
            sessions.ItemsSource = app.Snapshot?.Sessions.ToList();
            sessions.SelectedItem = app.Snapshot?.Sessions.FirstOrDefault(s => s.Url == selected) ?? app.Snapshot?.Sessions.FirstOrDefault();
            deviceLine.Text = "Device: " + (app.Snapshot?.Device?.Model ?? "not connected");
        }
        model.Refresh();
    }

    private void LoadScreenshots()
    {
        files.Clear();
        screenshots.Children.Clear();
        if (model.Session == null) return;
        foreach (var path in CaptureManager.ShotsFor(model.Session)) AddShot(path);
    }

    private void AddShot(string path)
    {
        if (files.Any(x => x.Path == path)) return;
        var check = new CheckBox { Content = System.IO.Path.GetFileName(path), IsChecked = files.Count(x => x.Box.IsChecked == true) < 4,
            Margin = new Thickness(0, 4, 0, 4) };
        check.Checked += (_, _) =>
        {
            if (files.Count(x => x.Box.IsChecked == true) > 4)
            {
                check.IsChecked = false;
                status.Text = "Select at most four screenshots.";
            }
        };
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(new Image { Source = new ImagePathConverter().Convert(path, typeof(ImageSource), "90", System.Globalization.CultureInfo.CurrentCulture) as ImageSource,
            Width = 100, Height = 90, Stretch = Stretch.Uniform });
        row.Children.Add(check);
        screenshots.Children.Add(PageUi.Card(row));
        files.Add((path, check));
    }

    private void AddFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(PageUi.Window) == true) AddShot(dialog.FileName);
    }

    private void ReviewSubmit()
    {
        if (model.Session == null) { status.Text = "Choose an app."; return; }
        if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(description.Text))
        { status.Text = "Enter a title and description."; return; }
        var count = files.Count(x => x.Box.IsChecked == true);
        status.Text = $"Send '{title.Text.Trim()}' to {model.Session.Partner} with {count} screenshot(s)? Reports cannot be unsent.";
        confirmSend.Visibility = Visibility.Visible;
    }

    private async Task SubmitAsync()
    {
        if (model.Session == null) { status.Text = "Choose an app."; return; }
        if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(description.Text))
        { status.Text = "Enter a title and description."; return; }
        List<string> selected;
        try { selected = files.Where(x => x.Box.IsChecked == true).Select(x => FitUnderLimit(x.Path)).ToList(); }
        catch (Exception ex) { status.Text = "Couldn't prepare screenshot: " + ex.Message; return; }
        status.Text = "Sending report…";
        confirmSend.IsEnabled = false;
        try
        {
            var error = await app.SubmitFeedbackAsync(model.Session, new FeedbackDraft
            {
                Type = (string)type.SelectedItem,
                Severity = (string)severity.SelectedItem,
                Title = title.Text.Trim(), Description = description.Text.Trim() + "\n\n" + deviceLine.Text, Files = selected
            });
            status.Text = error == null ? $"Report sent to {model.Session.Partner} ✓" : "Not sent: " + error;
        }
        catch (Exception ex) { status.Text = "Not sent: " + ex.Message; }
        finally { confirmSend.IsEnabled = true; confirmSend.Visibility = Visibility.Collapsed; }
    }

    private static string FitUnderLimit(string path)
    {
        const long max = 5 * 1024 * 1024;
        if (new System.IO.FileInfo(path).Length <= max) return path;
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TwelveTestersTray-" + Guid.NewGuid().ToString("N") + ".jpg");
        using (var stream = System.IO.File.Create(output)) encoder.Save(stream);
        if (new System.IO.FileInfo(output).Length > max) throw new InvalidOperationException("Screenshot is still larger than 5 MB after compression.");
        return output;
    }
}

public sealed class CheckinView : UserControl, IRefreshablePage
{
    private readonly TrayContext app;
    private readonly CheckinViewModel model;
    private readonly ComboBox sessions = new() { DisplayMemberPath = "App", MinWidth = 240, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel body = new();
    private readonly Image preview = new() { Height = 280, Stretch = Stretch.Uniform };
    private readonly TextBlock status = PageUi.Text("");
    private readonly TextBlock details = PageUi.Text("");
    private readonly CheckBox joined = new() { Content = "I joined the test", Margin = new Thickness(0, 4, 0, 10) };
    private readonly Button captureButton;
    private readonly Button uploadButton;
    private readonly Button confirmButton;
    private readonly Button pickButton;
    private string? file;
    private DateTime capturedAt;
    private bool busy;
    private bool canConfirm;
    private Snapshot? loadedSnapshot;

    public CheckinView(TrayContext app)
    {
        this.app = app;
        model = new CheckinViewModel(app);
        DataContext = model;
        Content = PageUi.Scroll(body);
        body.Children.Add(PageUi.Text("Capture a fresh screenshot and confirm the site's check-in."));
        body.Children.Add(sessions);
        body.Children.Add(joined);
        captureButton = PageUi.Button("Capture on phone / start daily check-in", () => _ = CaptureAsync());
        body.Children.Add(captureButton);
        pickButton = PageUi.Button("Use image file", PickFile);
        body.Children.Add(pickButton);
        body.Children.Add(preview);
        body.Children.Add(status);
        body.Children.Add(details);
        uploadButton = PageUi.Button("Check screenshot", () => _ = UploadAsync());
        confirmButton = PageUi.Button("Confirm check-in", () => _ = ConfirmAsync());
        body.Children.Add(uploadButton);
        body.Children.Add(confirmButton);
        body.Children.Add(PageUi.Button("Try another screenshot", Reset));
        sessions.SelectionChanged += (_, _) =>
        {
            if (busy) return;
            model.Session = sessions.SelectedItem as SessionInfo;
            joined.IsChecked = model.Session != null && app.Snapshot?.Device?.IsInstalled(model.Session.Package) == true;
            Reset();
            Refresh();
        };
        Refresh();
    }

    public void Select(SessionInfo session) { Refresh(); sessions.SelectedItem = session; }
    public bool TryLeave()
    {
        if (busy) { status.Text = "Wait for the current check-in step to finish."; return false; }
        app.CancelCheckin();
        canConfirm = false;
        return true;
    }
    public void Refresh()
    {
        if (!ReferenceEquals(loadedSnapshot, app.Snapshot))
        {
            loadedSnapshot = app.Snapshot;
            var selected = model.Session?.Url;
            sessions.ItemsSource = app.Snapshot?.Sessions.ToList();
            sessions.SelectedItem = app.Snapshot?.Sessions.FirstOrDefault(s => s.Url == selected) ?? app.Snapshot?.Sessions.FirstOrDefault();
        }
        model.Refresh();
        var daily = model.Session != null && !Snapshot.IsSetupStage(model.Session);
        joined.Visibility = daily ? Visibility.Collapsed : Visibility.Visible;
        pickButton.Visibility = daily ? Visibility.Collapsed : Visibility.Visible;
        uploadButton.Visibility = daily ? Visibility.Collapsed : Visibility.Visible;
        captureButton.Content = daily ? "Start daily check-in and capture" : "Capture on phone";
        captureButton.IsEnabled = !busy && model.Session?.Package != null;
        uploadButton.IsEnabled = !busy && file != null;
        confirmButton.IsEnabled = !busy && canConfirm;
    }

    private void Reset()
    {
        app.CancelCheckin();
        file = null;
        capturedAt = default;
        canConfirm = false;
        preview.Source = null;
        status.Text = "Choose a fresh screenshot with the phone clock visible.";
        details.Text = "";
        Refresh();
    }

    private void PickFile()
    {
        if (model.Session == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Screenshots|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(PageUi.Window) != true) return;
        SetFile(dialog.FileName, System.IO.File.GetLastWriteTime(dialog.FileName));
    }

    private void SetFile(string path, DateTime when)
    {
        file = path;
        capturedAt = when;
        preview.Source = new ImagePathConverter().Convert(path, typeof(ImageSource), "400", System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
        status.Text = $"Screenshot from {when:HH:mm:ss}; age {(int)(DateTime.Now - when).TotalMinutes} min.";
        if (DateTime.Now - when > TimeSpan.FromMinutes(10))
            details.Text = "This screenshot is old. The site may reject its clock check.";
        Refresh();
    }

    private async Task CaptureAsync()
    {
        var session = model.Session;
        if (session?.Package == null || busy) return;
        var daily = !Snapshot.IsSetupStage(session);
        busy = true; status.Text = daily ? "Starting site check-in…" : "Opening app and capturing…"; Refresh();
        try
        {
            if (daily)
            {
                var result = await app.BeginDailyCheckinAsync(session, async () =>
                {
                    var (png, error) = await app.Adb.CaptureAppAsync(session.Package);
                    if (error != null) return (null, DateTime.Now, error);
                    var path = SaveShot(session, png!, "checkin");
                    SetFile(path, capturedAt);
                    return (path, capturedAt, null);
                });
                ShowResult(result);
            }
            else
            {
                var (png, error) = await app.Adb.CaptureAppAsync(session.Package);
                if (error != null) status.Text = error;
                else SetFile(SaveShot(session, png!, "install"), capturedAt);
            }
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; Refresh(); }
    }

    private string SaveShot(SessionInfo session, byte[] png, string prefix)
    {
        var dir = System.IO.Path.Combine(CaptureManager.FolderFor(session), prefix);
        System.IO.Directory.CreateDirectory(dir);
        capturedAt = DateTime.Now;
        var path = System.IO.Path.Combine(dir, $"{prefix}-{capturedAt:yyyyMMdd-HHmmss}.png");
        System.IO.File.WriteAllBytes(path, png);
        return path;
    }

    private async Task UploadAsync()
    {
        if (model.Session == null || file == null || busy) return;
        busy = true; status.Text = "Checking screenshot on the site…"; Refresh();
        try { ShowResult(await app.BeginInstallCheckinAsync(model.Session, file, capturedAt, joined.IsChecked == true)); }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; Refresh(); }
    }

    private void ShowResult(InstallCheckResult result)
    {
        canConfirm = result.CanConfirm;
        status.Text = result.Stage == "done" ? "Checked in today ✓"
            : result.Stage == "join" ? "Join the Google Group and opt in, then select 'I joined the test' and check again."
            : result.CanConfirm ? (result.Confidence.Length == 0 ? "Check passed. Confirm when ready." : result.Confidence)
            : result.Message;
        details.Text = result.Details;
        if (result.Stage == "done" && model.Session != null) app.RecordCheckin(model.Session);
        Refresh();
    }

    private async Task ConfirmAsync()
    {
        if (!canConfirm || model.Session == null || busy) return;
        busy = true; status.Text = "Confirming…"; Refresh();
        try
        {
            var error = await app.ConfirmCheckinAsync();
            if (error == null) { status.Text = "Check-in saved ✓"; canConfirm = false; app.RecordCheckin(model.Session); }
            else status.Text = error;
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; Refresh(); }
    }
}

public sealed class AccountView : UserControl, IRefreshablePage
{
    private readonly TrayContext app;
    private readonly AccountViewModel model;
    private readonly TextBlock status = PageUi.Text("");
    private readonly TextBox email = new() { MinWidth = 300 };
    private readonly PasswordBox password = new() { MinWidth = 300 };
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 web = new() { Height = 500, Visibility = Visibility.Collapsed };
    public AccountView(TrayContext app)
    {
        this.app = app;
        model = new AccountViewModel(app);
        DataContext = model;
        var body = new StackPanel();
        Content = PageUi.Scroll(body);
        body.Children.Add(status);
        body.Children.Add(PageUi.Text("Email"));
        body.Children.Add(email);
        body.Children.Add(PageUi.Text("Password (leave empty to keep saved password)"));
        body.Children.Add(password);
        body.Children.Add(PageUi.Button("Save and sign in", Save));
        body.Children.Add(PageUi.Button("Remove saved login", Remove));
        body.Children.Add(PageUi.Button("Sign in manually", () => app.ShowManualSignIn()));
        body.Children.Add(PageUi.Button("Done signing in", () => _ = EndManualSignInAsync()));
        body.Children.Add(web);
        email.Text = CredentialStore.Load()?.Email ?? "";
        Refresh();
    }
    public void Refresh()
    {
        model.Refresh();
        status.Text = $"{(app.Snapshot?.SignedIn == true ? "Signed in" : "Not signed in")}. " +
            (app.Snapshot?.LoginError ?? (CredentialStore.Exists ? "A saved login is available." : "No login saved."));
    }

    private void Save()
    {
        var saved = CredentialStore.Load();
        if (!email.Text.Trim().Contains('@')) { status.Text = "Enter a valid email address."; return; }
        var value = password.Password.Length > 0 ? password.Password : saved?.Password ?? "";
        if (value.Length == 0) { status.Text = "Enter a password."; return; }
        CredentialStore.Save(email.Text.Trim(), value);
        password.Clear();
        app.ResetLoginFailure();
        status.Text = "Login saved. Checking account…";
        _ = app.CheckNowAsync();
    }

    private void Remove()
    {
        CredentialStore.Clear();
        password.Clear();
        app.ResetLoginFailure();
        status.Text = "Saved login removed.";
    }

    public async Task BeginManualSignInAsync()
    {
        try
        {
            status.Text = "Opening Twelve Testers sign-in…";
            await web.EnsureCoreWebView2Async(await app.BrowserEnvironment);
            app.IsManualSignInOpen = true;
            web.Visibility = Visibility.Visible;
            web.CoreWebView2.Navigate(SiteBrowser.Base + "/dashboard");
            status.Text = "Sign in below, then select Done signing in.";
        }
        catch (Exception ex) { status.Text = "Couldn't open sign-in: " + ex.Message; }
    }

    private async Task EndManualSignInAsync()
    {
        EndManualSignIn();
        status.Text = "Checking sign-in…";
        await app.CheckNowAsync();
    }

    public void EndManualSignIn()
    {
        web.Visibility = Visibility.Collapsed;
        app.IsManualSignInOpen = false;
    }
}

public sealed class SettingsView : UserControl, IRefreshablePage
{
    private readonly TrayContext app;
    private readonly SettingsViewModel model;
    private readonly StackPanel body = new();
    public SettingsView(TrayContext app)
    {
        this.app = app;
        model = new SettingsViewModel(app);
        DataContext = model;
        Content = PageUi.Scroll(body);
        Refresh();
    }

    public void Refresh()
    {
        model.Refresh();
        body.Children.Clear();
        body.Children.Add(PageUi.Text("Check interval (minutes)", 16));
        foreach (var minutes in new[] { 5, 15, 30, 60 })
            body.Children.Add(PageUi.Button($"{minutes}{(app.Settings.IntervalMinutes == minutes ? " ✓" : "")}", () =>
            { app.Settings.IntervalMinutes = minutes; app.Settings.Save(); app.ApplySettings(); Refresh(); }));
        body.Children.Add(PageUi.Text("Reminder times", 16));
        foreach (var time in new[] { "09:00", "10:00", "12:00", "15:00", "18:00", "19:00", "21:00" })
            body.Children.Add(PageUi.Button($"{time}{(app.Settings.ReminderTimes.Contains(time) ? " ✓" : "")}", () =>
            {
                if (!app.Settings.ReminderTimes.Remove(time)) app.Settings.ReminderTimes.Add(time);
                app.Settings.ReminderTimes.Sort(StringComparer.Ordinal); app.Settings.Save(); Refresh();
            }));
        body.Children.Add(PageUi.Text("Screenshot interval (seconds)", 16));
        foreach (var seconds in new[] { 5, 10, 15, 30, 60 })
            body.Children.Add(PageUi.Button($"{seconds}{(app.Settings.CaptureIntervalSeconds == seconds ? " ✓" : "")}", () =>
            { app.Settings.CaptureIntervalSeconds = seconds; app.Settings.Save(); Refresh(); }));
        body.Children.Add(PageUi.Button($"Phone checks: {(app.Settings.DeviceChecks ? "On" : "Off")}", () =>
        { app.Settings.DeviceChecks = !app.Settings.DeviceChecks; app.Settings.Save(); _ = app.CheckNowAsync(); Refresh(); }));
        var adb = new TextBox { Text = app.Settings.AdbPath ?? "", MinWidth = 320, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(PageUi.Text($"ADB path · Found at {app.Adb.AdbPath ?? "not found"}", 16));
        body.Children.Add(adb);
        body.Children.Add(PageUi.Button("Browse", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "adb.exe|adb.exe|Executables|*.exe" };
            if (dialog.ShowDialog(PageUi.Window) == true) adb.Text = dialog.FileName;
        }));
        body.Children.Add(PageUi.Button("Save ADB path", async () => { app.Settings.AdbPath = adb.Text.Trim(); app.Settings.Save(); app.Adb.Configure(app.Settings.AdbPath); await app.Adb.RefreshPathAsync(); Refresh(); }));
        body.Children.Add(PageUi.Button($"Theme: {app.Settings.Theme}", () =>
        { app.Settings.Theme = app.Settings.Theme == "Dark" ? "Light" : "Dark"; app.Settings.Save(); ThemeManager.Apply(app.Settings.Theme); Refresh(); }));
        body.Children.Add(PageUi.Button($"Start with Windows: {(StartupRegistration.IsEnabled ? "On" : "Off")}", () =>
        { StartupRegistration.Set(!StartupRegistration.IsEnabled); Refresh(); }));
        body.Children.Add(PageUi.Button($"Close to tray: {(app.Settings.CloseToTray ? "On" : "Off")}", () =>
        { app.Settings.CloseToTray = !app.Settings.CloseToTray; app.Settings.Save(); Refresh(); }));
        body.Children.Add(PageUi.Button("Open data folder", () => Process.Start(new ProcessStartInfo(Settings.Dir) { UseShellExecute = true })));
        body.Children.Add(PageUi.Button("Open log", () => Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Settings.Dir, "log.txt")) { UseShellExecute = true })));
    }
}
