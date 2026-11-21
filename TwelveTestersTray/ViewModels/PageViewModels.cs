using TwelveTestersTray.Services;

namespace TwelveTestersTray.ViewModels;

public abstract class PageViewModel(TrayContext app) : ObservableObject
{
    protected TrayContext App { get; } = app;
    public Snapshot? Snapshot => App.Snapshot;
    public Settings Settings => App.Settings;
    public virtual void Refresh() => OnPropertyChanged(nameof(Snapshot));
}

public sealed class TodayViewModel(TrayContext app) : PageViewModel(app)
{
    public IReadOnlyList<TodoItem> Todos => App.Todos;
    public override void Refresh() { base.Refresh(); OnPropertyChanged(nameof(Todos)); }
}

public sealed class TestingViewModel(TrayContext app) : PageViewModel(app)
{
    public CaptureRun? Run => App.Capture.Current;
    public override void Refresh() { base.Refresh(); OnPropertyChanged(nameof(Run)); }
}

public sealed class ReportViewModel(TrayContext app) : PageViewModel(app)
{
    public SessionInfo? Session { get; set; }
}

public sealed class CheckinViewModel(TrayContext app) : PageViewModel(app)
{
    public SessionInfo? Session { get; set; }
}

public sealed class AccountViewModel(TrayContext app) : PageViewModel(app);
public sealed class SettingsViewModel(TrayContext app) : PageViewModel(app);
