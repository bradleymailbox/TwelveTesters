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

    /// <summary>Opens Explorer with the file selected (or its folder if the file is gone).</summary>
    public static void OpenFolder(string file)
    {
        try
        {
            if (File.Exists(file)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{file}\"", UseShellExecute = false });
            else if (System.IO.Path.GetDirectoryName(file) is { } dir && Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* Explorer unavailable */ }
    }

    public static bool CopyText(string text)
    {
        try { System.Windows.Clipboard.SetText(text); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; /* clipboard busy */ }
    }

    /// <summary>Colours a button by meaning: success (green), danger (red), info (cyan), warn (amber), accent (orange).</summary>
    public static void Tint(Button button, string? tint)
    {
        var brush = tint switch { "success" => "SuccessBrush", "danger" => "ErrorBrush", "info" => "CyanBrush", "warn" => "WarningBrush", "accent" => "OrangeBrush", _ => null };
        if (brush == null) return;
        button.SetResourceReference(FrameworkElement.StyleProperty, "TintButton");
        button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, brush);
    }

    /// <summary>Tints chosen options and "On" toggles green so the current setting stands out.</summary>
    public static void TintSelected(System.Windows.Controls.Panel panel)
    {
        foreach (var button in panel.Children.OfType<Button>())
            if (button.Content is string text && (text.EndsWith("✓") || text.EndsWith(": On"))) Tint(button, "success");
    }

    /// <summary>A collapsible section: clickable header (▾/▸, title, count) plus a content panel. The open/closed
    /// state is remembered in settings. Returns the panel to fill.</summary>
    public static StackPanel AddSection(System.Windows.Controls.Panel parent, Settings settings, string key, string title, int? count = null)
    {
        var content = new StackPanel();
        var arrow = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 13 };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "CyanBrush");
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(FrameworkElement.StyleProperty, "SectionLabel");
        label.Margin = new Thickness(0);
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        header.Children.Add(arrow);
        header.Children.Add(label);
        if (count != null)
        {
            var badge = new TextBlock { Text = count.ToString(), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            header.Children.Add(badge);
        }
        var button = new Button { Content = header, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 4),
            Padding = new Thickness(4, 2, 10, 2), MinWidth = 0, ToolTip = "Click to collapse or expand" };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        void Apply()
        {
            var collapsed = settings.CollapsedSections.Contains(key);
            arrow.Text = collapsed ? "▸" : "▾";
            content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        button.Click += (_, _) =>
        {
            if (!settings.CollapsedSections.Remove(key)) settings.CollapsedSections.Add(key);
            settings.Save();
            Apply();
        };
        Apply();
        parent.Children.Add(button);
        parent.Children.Add(content);
        return content;
    }

    public static Button Button(string label, Action action, string? tint = null)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8), MinWidth = 90,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        button.Click += (_, _) => action();
        Tint(button, tint);
        return button;
    }

    public static Border Card(UIElement child)
    {
        var border = new Border
        {
            Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12)
        };
        border.SetResourceReference(Border.StyleProperty, "Card");
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        return border;
    }

    /// <summary>Scrolling page body, left-aligned and capped at a readable width so fields and cards don't span a wide window.</summary>
    public static ScrollViewer Scroll(StackPanel content, double maxWidth = 980)
    {
        var column = new Grid();
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth });
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        column.Children.Add(content);
        return new ScrollViewer { Content = column, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
    }

    /// <summary>Tight card for list rows (Today, Testing).</summary>
    public static Border CompactCard(UIElement child)
    {
        var border = new Border
        {
            Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6)
        };
        border.SetResourceReference(Border.StyleProperty, "Card");
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        border.CornerRadius = new CornerRadius(10);
        border.Padding = new Thickness(12, 8, 12, 8);
        return border;
    }

    /// <summary>One tight line of text (muted by default) for list rows.</summary>
    public static TextBlock Line(string value, bool muted = true, double size = 12, FontWeight? weight = null)
    {
        var line = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        if (weight != null) line.FontWeight = weight.Value;
        line.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
        return line;
    }

    /// <summary>A small button for dense rows.</summary>
    public static Button Small(string label, Action action, string? tint = null)
    {
        var button = Button(label, action, tint);
        button.Padding = new Thickness(10, 3, 10, 3);
        button.MinWidth = 0;
        button.FontSize = 12;
        button.Margin = new Thickness(0, 2, 6, 2);
        return button;
    }

    /// <summary>A small heading for a group of controls.</summary>
    public static TextBlock Heading(string value)
    {
        var heading = new TextBlock { Text = value, Margin = new Thickness(0, 14, 0, 6) };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "SectionLabel");
        return heading;
    }
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
        Content = PageUi.Scroll(body, double.PositiveInfinity);
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
                error.Children.Add(PageUi.Button("Download WebView2 Runtime", () => TrayContext.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/"), "info"));
            var retry = PageUi.Button("Retry", () => _ = app.CheckNowAsync(), "info");
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
            login.Children.Add(PageUi.Button("Fix login", () => PageUi.Window.Navigate("Account"), "warn"));
            body.Children.Add(PageUi.Card(login));
            return;
        }

        if (app.Settings.DeviceChecks && snap.Device is not { Connected: true })
        {
            var phone = new StackPanel();
            phone.Children.Add(PageUi.Text(snap.Device is { AdbFound: false } ? "ADB not found" : "No phone connected", 18));
            phone.Children.Add(PageUi.Text(snap.Device?.Problem ?? "Connect and unlock your phone, enable USB debugging, and accept the USB authorization prompt."));
            phone.Children.Add(PageUi.Text("ADB path: " + (app.Adb.AdbPath ?? app.Settings.AdbPath ?? "Not configured")));
            phone.Children.Add(PageUi.Button("Configure ADB", () => PageUi.Window.Navigate("Settings"), "info"));
            var retry = PageUi.Button("Retry phone check", () => _ = app.CheckNowAsync(), "info");
            retry.IsEnabled = !app.IsChecking;
            phone.Children.Add(retry);
            body.Children.Add(PageUi.Card(phone));
        }

        var todoList = PageUi.AddSection(body, app.Settings, "today.todo", "To do", app.Todos.Count);
        if (app.Error == null && !app.Todos.Any(t => t.Priority != Priority.Low))
            todoList.Children.Add(PageUi.Card(PageUi.Text("All done for today ✓")));
        foreach (var todo in app.Todos)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(PageUi.Line(todo.Title, muted: false, size: 14, weight: FontWeights.SemiBold));
            text.Children.Add(PageUi.Line(todo.Detail));
            row.Children.Add(text);
            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            buttons.Children.Add(PageUi.Small("Open", () => app.OpenItem(todo), "info"));
            var dismiss = PageUi.Small("Dismiss", () => app.DismissTodo(todo));
            dismiss.ToolTip = "Already done? Hide this until it changes.";
            buttons.Children.Add(dismiss);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            todoList.Children.Add(PageUi.CompactCard(row));
        }
        if (app.HiddenTodoCount > 0)
        {
            var restore = PageUi.Small($"Show {app.HiddenTodoCount} dismissed", () => app.RestoreAllTodos());
            restore.ToolTip = "Bring back everything you dismissed.";
            todoList.Children.Add(restore);
        }

        var swapList = PageUi.AddSection(body, app.Settings, "today.swaps", "Swaps", snap.ActiveSessions.Count());
        foreach (var session in snap.ActiveSessions)
        {
            var state = SessionCardState.From(session, snap);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var thumb = TodayThumbnail(session);
            if (thumb != null) { thumb.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(thumb); }

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            header.Children.Add(PageUi.Line(session.App, muted: false, size: 14, weight: FontWeights.SemiBold));
            var chipText = new TextBlock { Text = state.Stage, FontSize = 11, FontWeight = FontWeights.SemiBold };
            chipText.SetResourceReference(TextBlock.ForegroundProperty, state.BrushKey);
            var chip = new Border { Child = chipText, Margin = new Thickness(8, 1, 0, 0), Padding = new Thickness(8, 1, 8, 1), VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(Border.StyleProperty, "Chip");
            chip.Margin = new Thickness(8, 1, 0, 0);
            header.Children.Add(chip);
            info.Children.Add(header);
            info.Children.Add(PageUi.Line($"{session.Partner}  ·  {session.DaysLeft}  ·  {session.MyStatus}"));
            info.Children.Add(PageUi.Line($"Installed {state.Installed}  ·  Opened today {state.Opened}  ·  Check-in {state.Checkin}"));
            var packageKnown = !string.IsNullOrWhiteSpace(session.Package);
            if (!packageKnown) info.Children.Add(PageUi.Line("Package unknown — run Check now or open the session page to finish setup."));
            Grid.SetColumn(info, 1);
            row.Children.Add(info);

            var actions = new WrapPanel { MaxWidth = 700, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            // Actions follow the phone: nothing to install once the app is there, and no testing or check-in before it is.
            var installed = state.Installed == "✓";
            var missing = state.Installed == "✗";
            const string installFirst = "Install the app on the phone first.";
            if (!installed)
            {
                var install = PageUi.Small("Install on phone", () => _ = InstallAsync(session), "accent");
                install.IsEnabled = packageKnown;
                install.ToolTip = packageKnown ? null : "The app's package name is not known yet.";
                actions.Children.Add(install);
            }
            var start = PageUi.Small("Start testing", () => _ = StartAsync(session), "info");
            start.IsEnabled = packageKnown && !missing;
            start.ToolTip = !packageKnown ? "The app's package name is not known yet." : missing ? installFirst : null;
            actions.Children.Add(start);
            if (app.Capture.IsRunning(session))
                actions.Children.Add(PageUi.Small("Stop", () => { app.Capture.Stop(); PageUi.Window.Navigate("Testing", session); }, "danger"));
            var dayOne = state.Stage == "Day 1 check-in";
            var checkIn = PageUi.Small(dayOne ? "Capture & check in" : "Check in", () => PageUi.Window.Navigate("Checkin", session, autoStart: true), dayOne ? "success" : "warn");
            checkIn.IsEnabled = !missing;
            checkIn.ToolTip = missing ? installFirst : "Opens the app on the phone and takes the screenshot for the check-in.";
            actions.Children.Add(checkIn);
            actions.Children.Add(PageUi.Small("Mark tested", () => { app.Settings.Confirm(session.Url); app.RefreshFromSettings(); }, "success"));
            actions.Children.Add(PageUi.Small("Report", () => PageUi.Window.Navigate("Report", session)));
            var shot = PageUi.Small("Screenshot", () => _ = ScreenshotAsync(session), "info");
            shot.ToolTip = "Capture what is on the phone screen now, save it in this app's folder and copy its path.";
            actions.Children.Add(shot);
            var more = PageUi.Small("⋯", () => { });
            var menu = new ContextMenu();
            var sessionLink = new MenuItem { Header = "Open session page" };
            sessionLink.Click += (_, _) => TrayContext.OpenUrl(session.Url);
            menu.Items.Add(sessionLink);
            var unmark = new MenuItem { Header = "Unmark tested" };
            unmark.Click += (_, _) => { app.Settings.Unconfirm(session.Url); app.RefreshFromSettings(); };
            menu.Items.Add(unmark);
            more.Click += (_, _) => menu.IsOpen = true;
            actions.Children.Add(more);
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);

            var card = PageUi.CompactCard(row);
            card.SetResourceReference(Border.BorderBrushProperty, state.BrushKey);
            swapList.Children.Add(card);
        }
    }

    private static Image? TodayThumbnail(SessionInfo session)
    {
        try
        {
            var path = CaptureManager.ShotsFor(session).FirstOrDefault(p => File.GetLastWriteTime(p).Date == DateTime.Today);
            if (path == null) return null;
            var source = new ImagePathConverter().Convert(path, typeof(ImageSource), "120", System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
            if (source == null) return null;
            return new Image { Source = source, Height = 60, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Latest capture today · {File.GetLastWriteTime(path):HH:mm}" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException)
        { return null; /* A missing or incomplete image must not prevent the app card from rendering. */ }
    }

    private async Task StartAsync(SessionInfo session)
    {
        var error = await app.Capture.StartAsync(session);
        if (error != null) app.ShowNotice(error);
        else PageUi.Window.ShowFromTray("Testing");
    }

    private async Task ScreenshotAsync(SessionInfo session)
    {
        var (file, error) = await app.Capture.CaptureNowAsync(session);
        if (file == null) { app.ShowNotice(error ?? "Couldn't capture the phone screen."); return; }
        var copied = PageUi.CopyText(file);
        PageUi.Window.ShowNotice($"Screenshot of {session.App} saved:\n{file}\n{(copied ? "The path is copied to the clipboard." : "Use Open folder to find it.")}",
            false, "Open folder", () => PageUi.OpenFolder(file));
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
        Content = PageUi.Scroll(body, double.PositiveInfinity);
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
        var files = CaptureManager.ShotsFor(run.Session);
        var card = new Grid();
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (files.Count > 0 && new ImagePathConverter().Convert(files[0], typeof(ImageSource), "220", System.Globalization.CultureInfo.CurrentCulture) is ImageSource source)
            card.Children.Add(new Image { Source = source, Height = 130, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(PageUi.Line(run.Session.App, muted: false, size: 16, weight: FontWeights.SemiBold));
        info.Children.Add(PageUi.Line($"{(run.Running ? "Testing" : "Stopped")}  ·  {elapsed:mm\\:ss} / 45:00  ·  {run.Count} screenshot(s)"));
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (run.Running) actions.Children.Add(PageUi.Small("Stop testing", () => { app.Capture.Stop(); Refresh(); }, "danger"));
        else
        {
            actions.Children.Add(PageUi.Small("Mark tested", () => { app.Settings.Confirm(run.Session.Url); app.RefreshFromSettings(); }, "success"));
            actions.Children.Add(PageUi.Small("Write report", () => PageUi.Window.Navigate("Report", run.Session)));
            actions.Children.Add(PageUi.Small("Check in", () => PageUi.Window.Navigate("Checkin", run.Session), "warn"));
            actions.Children.Add(PageUi.Small("Done", () => PageUi.Window.Navigate("Today")));
        }
        info.Children.Add(actions);
        if (!run.Running && run.Problem != null) info.Children.Add(PageUi.Line(run.Problem));
        Grid.SetColumn(info, 1);
        card.Children.Add(info);
        body.Children.Add(PageUi.CompactCard(card));
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
        var fields = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (label, box) in new (string, FrameworkElement)[] { ("Type", type), ("Severity", severity) })
        {
            var group = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            group.Children.Add(PageUi.Text(label));
            group.Children.Add(box);
            fields.Children.Add(group);
        }
        body.Children.Add(fields);
        body.Children.Add(PageUi.Text("Title"));
        body.Children.Add(title);
        body.Children.Add(PageUi.Text("Description"));
        body.Children.Add(description);
        body.Children.Add(deviceLine);
        body.Children.Add(PageUi.Text("Screenshots (up to 4)"));
        body.Children.Add(screenshots);
        var reportActions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(reportActions);
        reportActions.Children.Add(PageUi.Button("Add file", AddFile));
        reportActions.Children.Add(PageUi.Button("Review and submit", ReviewSubmit, "info"));
        confirmSend = PageUi.Button("Confirm submission", () => _ = SubmitAsync(), "success");
        confirmSend.Visibility = Visibility.Collapsed;
        reportActions.Children.Add(confirmSend);
        reportActions.Children.Add(PageUi.Button("Open site form instead", () => { if (model.Session != null) TrayContext.OpenUrl(model.Session.FeedbackUrl); }));
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
        // After a testing run, pre-tick that run's screenshots (newest first, at most four);
        // with no run for this app, fall back to the newest screenshots.
        var run = app.Capture.Current is { } current && current.Session.Url == model.Session.Url ? current : null;
        runFolder = run?.Folder;
        var all = CaptureManager.ShotsFor(model.Session);
        var runHasShots = all.Any(IsFromRun);
        if (runHasShots) all = all.Where(IsFromRun).Concat(all.Where(p => !IsFromRun(p))).ToList();
        foreach (var path in all) AddShot(path, tick: !runHasShots || IsFromRun(path));
    }

    private string? runFolder;
    private bool IsFromRun(string path) =>
        runFolder != null && path.StartsWith(runFolder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void AddShot(string path, bool tick = true)
    {
        if (files.Any(x => x.Path == path)) return;
        var check = new CheckBox { Content = System.IO.Path.GetFileName(path),
            IsChecked = tick && files.Count(x => x.Box.IsChecked == true) < 4,
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
    private readonly Button captureScreenButton;
    private readonly TextBox pathBox = new() { IsReadOnly = true, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel pathPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
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
        var capture = new WrapPanel();
        body.Children.Add(capture);
        captureButton = PageUi.Button("Capture on phone / start daily check-in", () => _ = CaptureAsync(), "info");
        capture.Children.Add(captureButton);
        captureScreenButton = PageUi.Button("Capture phone screenshot", () => _ = CaptureScreenAsync(), "info");
        captureScreenButton.ToolTip = "Capture whatever is on the phone screen now.";
        capture.Children.Add(captureScreenButton);
        pickButton = PageUi.Button("Use image file", PickFile);
        capture.Children.Add(pickButton);
        capture.Children.Add(PageUi.Button("Open session page", () => { if (model.Session != null) TrayContext.OpenUrl(model.Session.Url); }));
        body.Children.Add(preview);
        body.Children.Add(status);
        pathPanel.Children.Add(PageUi.Text("Screenshot file — choose this in the site's upload dialog:"));
        pathPanel.Children.Add(pathBox);
        var pathActions = new WrapPanel();
        pathActions.Children.Add(PageUi.Button("Open folder", () => { if (file != null) PageUi.OpenFolder(file); }, "info"));
        pathActions.Children.Add(PageUi.Button("Copy path", () => { if (file != null) details.Text = PageUi.CopyText(file) ? "Path copied to the clipboard." : "Couldn't use the clipboard."; }));
        pathPanel.Children.Add(pathActions);
        body.Children.Add(pathPanel);
        body.Children.Add(details);
        var steps = new WrapPanel();
        body.Children.Add(steps);
        uploadButton = PageUi.Button("Check screenshot", () => _ = UploadAsync(), "info");
        confirmButton = PageUi.Button("Confirm check-in", () => _ = ConfirmAsync(), "success");
        steps.Children.Add(uploadButton);
        steps.Children.Add(confirmButton);
        steps.Children.Add(PageUi.Button("Try another screenshot", Reset));
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

    /// <param name="autoCapture">Open the app on the phone and take the screenshot immediately (no second button press).</param>
    public void Select(SessionInfo session, bool autoCapture = false)
    {
        Refresh();
        sessions.SelectedItem = session;
        if (autoCapture && !busy && session.Package != null)
            Dispatcher.BeginInvoke(new Action(() => { if (model.Session?.Url == session.Url) _ = CaptureAsync(); }),
                System.Windows.Threading.DispatcherPriority.Background);
    }
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
        captureScreenButton.IsEnabled = !busy && model.Session != null;
        uploadButton.IsEnabled = !busy && file != null;
        confirmButton.IsEnabled = !busy && canConfirm;
    }

    private void Reset()
    {
        app.CancelCheckin();
        file = null;
        capturedAt = default;
        pathBox.Text = "";
        pathPanel.Visibility = Visibility.Collapsed;
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
        pathBox.Text = path;
        pathPanel.Visibility = Visibility.Visible;
        preview.Source = new ImagePathConverter().Convert(path, typeof(ImageSource), "400", System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
        status.Text = $"Screenshot from {when:HH:mm:ss}; age {(int)(DateTime.Now - when).TotalMinutes} min.";
        if (DateTime.Now - when > TimeSpan.FromMinutes(10))
            details.Text = "This screenshot is old. The site may reject its clock check.";
        Refresh();
    }

    private async Task CaptureScreenAsync()
    {
        var session = model.Session;
        if (session == null || busy) return;
        busy = true; status.Text = "Capturing the phone screen…"; Refresh();
        try
        {
            var (path, error) = await app.Capture.CaptureNowAsync(session);
            if (path == null) status.Text = error ?? "Couldn't capture the phone screen.";
            else
            {
                SetFile(path, DateTime.Now);
                details.Text = PageUi.CopyText(path) ? "Path copied to the clipboard." : "";
            }
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; Refresh(); }
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
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(actions);
        actions.Children.Add(PageUi.Button("Save and sign in", Save, "success"));
        actions.Children.Add(PageUi.Button("Remove saved login", Remove, "danger"));
        actions.Children.Add(PageUi.Button("Sign in manually", () => app.ShowManualSignIn(), "info"));
        actions.Children.Add(PageUi.Button("Done signing in", () => _ = EndManualSignInAsync()));
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
        StackPanel Sec(string key, string title) => PageUi.AddSection(body, app.Settings, "settings." + key, title);
        var intervals = new WrapPanel();
        Sec("interval", "Check interval (minutes)").Children.Add(intervals);
        foreach (var minutes in new[] { 5, 15, 30, 60 })
            intervals.Children.Add(PageUi.Button($"{minutes}{(app.Settings.IntervalMinutes == minutes ? " ✓" : "")}", () =>
            { app.Settings.IntervalMinutes = minutes; app.Settings.Save(); app.ApplySettings(); Refresh(); }));
        var reminders = new WrapPanel();
        Sec("reminders", "Reminder times").Children.Add(reminders);
        foreach (var time in new[] { "09:00", "10:00", "12:00", "15:00", "18:00", "19:00", "21:00" })
            reminders.Children.Add(PageUi.Button($"{time}{(app.Settings.ReminderTimes.Contains(time) ? " ✓" : "")}", () =>
            {
                if (!app.Settings.ReminderTimes.Remove(time)) app.Settings.ReminderTimes.Add(time);
                app.Settings.ReminderTimes.Sort(StringComparer.Ordinal); app.Settings.Save(); Refresh();
            }));
        var shots = new WrapPanel();
        Sec("shots", "Screenshot interval (seconds)").Children.Add(shots);
        foreach (var seconds in new[] { 5, 10, 15, 30, 60 })
            shots.Children.Add(PageUi.Button($"{seconds}{(app.Settings.CaptureIntervalSeconds == seconds ? " ✓" : "")}", () =>
            { app.Settings.CaptureIntervalSeconds = seconds; app.Settings.Save(); Refresh(); }));
        var phoneSection = Sec("phone", "Phone");
        phoneSection.Children.Add(PageUi.Button($"Phone checks: {(app.Settings.DeviceChecks ? "On" : "Off")}", () =>
        { app.Settings.DeviceChecks = !app.Settings.DeviceChecks; app.Settings.Save(); _ = app.CheckNowAsync(); Refresh(); }));
        var adb = new TextBox { Text = app.Settings.AdbPath ?? "", Tag = "Auto-detect (leave empty)", MinWidth = 320, Margin = new Thickness(0, 0, 0, 8) };
        phoneSection.Children.Add(PageUi.Text($"ADB path · Found at {app.Adb.AdbPath ?? "not found"}"));
        phoneSection.Children.Add(adb);
        var adbButtons = new WrapPanel();
        phoneSection.Children.Add(adbButtons);
        adbButtons.Children.Add(PageUi.Button("Browse", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "adb.exe|adb.exe|Executables|*.exe" };
            if (dialog.ShowDialog(PageUi.Window) == true) adb.Text = dialog.FileName;
        }));
        adbButtons.Children.Add(PageUi.Button("Save ADB path", async () => { app.Settings.AdbPath = adb.Text.Trim(); app.Settings.Save(); app.Adb.Configure(app.Settings.AdbPath); await app.Adb.RefreshPathAsync(); Refresh(); }));
        var appButtons = new WrapPanel();
        Sec("app", "App").Children.Add(appButtons);
        appButtons.Children.Add(PageUi.Button($"Theme: {app.Settings.Theme}", () =>
        { app.Settings.Theme = app.Settings.Theme == "Dark" ? "Light" : "Dark"; app.Settings.Save(); ThemeManager.Apply(app.Settings.Theme); Refresh(); }));
        appButtons.Children.Add(PageUi.Button($"Start with Windows: {(StartupRegistration.IsEnabled ? "On" : "Off")}", () =>
        { StartupRegistration.Set(!StartupRegistration.IsEnabled); Refresh(); }));
        appButtons.Children.Add(PageUi.Button($"Close to tray: {(app.Settings.CloseToTray ? "On" : "Off")}", () =>
        { app.Settings.CloseToTray = !app.Settings.CloseToTray; app.Settings.Save(); Refresh(); }));
        appButtons.Children.Add(PageUi.Button("Open data folder", () => Process.Start(new ProcessStartInfo(Settings.Dir) { UseShellExecute = true })));
        PageUi.TintSelected(intervals);
        PageUi.TintSelected(reminders);
        PageUi.TintSelected(shots);
        PageUi.TintSelected(phoneSection);
        PageUi.TintSelected(appButtons);
        appButtons.Children.Add(PageUi.Button("Open log", () => Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Settings.Dir, "log.txt")) { UseShellExecute = true })));
    }
}
