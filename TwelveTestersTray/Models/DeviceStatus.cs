namespace TwelveTestersTray;

public class DeviceStatus
{
    public bool AdbFound { get; set; }
    public bool Connected { get; set; }
    public string? Model { get; set; }
    public string? Problem { get; set; }
    public HashSet<string> Installed { get; set; } = new();
    public Dictionary<string, DateTime> LastOpened { get; set; } = new();
    public string Name => string.IsNullOrEmpty(Model) ? "your phone" : Model!;
    public bool IsInstalled(string? pkg) => Connected && pkg != null && Installed.Contains(pkg);
    public bool IsMissing(string? pkg) => Connected && pkg != null && !Installed.Contains(pkg);
    public bool OpenedToday(string? pkg) =>
        pkg != null && LastOpened.TryGetValue(pkg, out var time) && time.Date == DateTime.Today;
}
