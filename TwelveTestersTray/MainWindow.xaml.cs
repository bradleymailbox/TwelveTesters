using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using TwelveTestersTray.Views;
using TwelveTestersTray.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace TwelveTestersTray;

public partial class MainWindow : Window
{
    private TrayContext? controller;
    private readonly Dictionary<string, UserControl> pages = new();
    public bool AllowClose { get; set; }
    private bool placementReady;
    private bool wasMaximized;
    private readonly System.Windows.Threading.DispatcherTimer boundsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private readonly System.Windows.Threading.DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    public MainWindow()
    {
        InitializeComponent();
        FontFamily = (System.Windows.Media.FontFamily)FindResource("BodyFont");
        statusTimer.Tick += (_, _) => RefreshHeader();
        statusTimer.Start();
        SourceInitialized += (_, _) =>
        {
            WindowPlacement.Restore(this, controller?.Settings.WindowBounds);
            wasMaximized = WindowState == WindowState.Maximized;
        };
        Loaded += (_, _) => placementReady = true;
        LocationChanged += (_, _) => QueueSaveBounds();
        SizeChanged += (_, _) => QueueSaveBounds();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) wasMaximized = WindowState == WindowState.Maximized;
            QueueSaveBounds();
        };
        boundsTimer.Tick += (_, _) => SaveBounds();
    }

    public void Attach(TrayContext context)
    {
        controller = context;
        pages["Today"] = new TodayView(context);
        pages["Testing"] = new TestingView(context);
        pages["Report"] = new ReportView(context);
        pages["Checkin"] = new CheckinView(context);
        pages["Account"] = new AccountView(context);
        pages["Settings"] = new SettingsView(context);
        context.StateChanged += Refresh;
        Navigate(context.Settings.LastPage);
    }

    public void Navigate(string page, SessionInfo? session = null)
    {
        if (controller == null) return;
        if (PageHost.Content is CheckinView currentCheckin && page != "Checkin" && !currentCheckin.TryLeave()) return;
        if (PageHost.Content is AccountView currentAccount && page != "Account") currentAccount.EndManualSignIn();
        if (!pages.TryGetValue(page, out var view)) { page = "Today"; view = pages[page]; }
        PageTitle.Text = page == "Checkin" ? "Check-in" : page;
        PageHost.Content = view;
        controller.Settings.LastPage = page;
        controller.Settings.Save();
        if (session != null)
        {
            if (view is ReportView report) report.Select(session);
            if (view is CheckinView checkin) checkin.Select(session);
        }
        Refresh();
    }

    public void ShowFromTray(string page = "Today", SessionInfo? session = null)
    {
        Navigate(page, session);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = wasMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void BeginManualSignIn()
    {
        ShowFromTray("Account");
        if (PageHost.Content is AccountView account) _ = account.BeginManualSignInAsync();
    }

    public void Refresh()
    {
        RefreshHeader();
        foreach (var view in pages.Values)
            if (view is IRefreshablePage refreshable) refreshable.Refresh();
    }

    private void RefreshHeader()
    {
        if (controller == null) return;
        var snap = controller.Snapshot;
        StatusLine.Text = controller.IsChecking ? "Checking…"
            : controller.Error != null ? controller.Error
            : snap == null ? "Waiting for first check"
            : $"{(snap.SignedIn ? "Signed in" : "Login needed")}  ·  {snap.Credits?.ToString() ?? "–"} credits  ·  {snap.DueToday} due  ·  {snap.CheckinsDue} check-ins  ·  Checked {snap.At:t}";
        if (snap != null && !controller.IsChecking && controller.Error == null)
            StatusLine.Text = $"{snap.Credits?.ToString() ?? "–"} credits · {snap.DueToday} due · {snap.CheckinsDue} check-ins · {snap.FeedbackIn} feedback · {snap.Requests.Count} requests · Checked {Math.Max(0, (int)(DateTime.Now - snap.At).TotalMinutes)} min ago";
        StatusLine.TextWrapping = TextWrapping.Wrap;
        LoginChip.Text = snap == null ? "Awaiting first check" : snap.SignedIn ? "Signed in" : "Login needed";
        LoginChip.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, snap?.SignedIn == true ? "SuccessBrush" : "WarningBrush");
        PhoneLine.Text = !controller.Settings.DeviceChecks ? "Phone checks off"
            : snap == null ? "Phone: awaiting first check"
            : snap?.Device is { Connected: true } phone ? "Phone: " + phone.Name
            : snap?.Device is { AdbFound: false } ? "ADB not found" : "No phone connected";
        RefreshButton.IsEnabled = !controller.IsChecking;
        RefreshButton.Content = controller.IsChecking ? "↻ Checking…" : "↻ Check now";
        if (controller.IsChecking)
        {
            var rotation = new System.Windows.Media.RotateTransform();
            RefreshButton.RenderTransformOrigin = new System.Windows.Point(.5, .5);
            RefreshButton.RenderTransform = rotation;
            rotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        }
        else RefreshButton.RenderTransform = System.Windows.Media.Transform.Identity;
    }

    public void ShowNotice(string message, bool confirmExit = false)
    {
        NoticeText.Text = message;
        NoticePanel.Visibility = Visibility.Visible;
        ConfirmExitButton.Visibility = confirmExit ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ConfirmExit_Click(object sender, RoutedEventArgs e) => controller?.ExitApplication(true);
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => NoticePanel.Visibility = Visibility.Collapsed;

    private void Navigate_Click(object sender, RoutedEventArgs e) => Navigate((string)((Button)sender).Tag);
    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        if (controller != null) await controller.CheckNowAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();
        if (!AllowClose && (controller?.Settings.CloseToTray ?? true))
        {
            e.Cancel = true;
            if (PageHost.Content is AccountView account) account.EndManualSignIn();
            Hide();
            controller?.OnHiddenToTray();
        }
        else if (!AllowClose && controller != null)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => controller.ExitApplication());
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        statusTimer.Stop();
        boundsTimer.Stop();
        if (!AllowClose) controller?.ExitApplication();
    }

    private void QueueSaveBounds()
    {
        if (!placementReady || !IsVisible || controller == null) return;
        boundsTimer.Stop();
        boundsTimer.Start();
    }

    private void SaveBounds()
    {
        boundsTimer.Stop();
        if (!placementReady || controller == null) return;
        var bounds = WindowPlacement.Capture(this, wasMaximized);
        if (bounds == null) return;
        controller.Settings.WindowBounds = bounds;
        controller.Settings.Save();
    }
}
