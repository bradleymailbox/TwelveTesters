using System.Security.Cryptography;

namespace TwelveTestersTray;

public class CaptureRun
{
    public SessionInfo Session { get; init; } = new();
    public string Folder { get; init; } = "";
    public DateTime Started { get; init; } = DateTime.Now;
    public int Count { get; set; }
    public bool Running { get; set; }
    public string? Problem { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// "Start testing" mode: opens the app on the phone and screenshots it every few seconds
/// while that app is on screen. Screenshots are saved per app for use in feedback reports.
/// </summary>
public class CaptureManager
{
    readonly Func<string?> adbPath;
    readonly Func<int> intervalSeconds;
    readonly SynchronizationContext? ui;

    public static readonly TimeSpan MaxRun = TimeSpan.FromMinutes(45);
    public static string Root => Path.Combine(Settings.Dir, "captures");

    public CaptureRun? Current { get; private set; }

    /// <summary>Raised (on the UI thread) whenever a run starts, captures, or stops.</summary>
    public event Action<CaptureRun>? Changed;
    /// <summary>Raised (on the UI thread) when a run ends by itself (time limit / phone gone).</summary>
    public event Action<CaptureRun>? EndedAutomatically;

    public CaptureManager(Func<string?> adbPath, Func<int> intervalSeconds)
    {
        this.adbPath = adbPath;
        this.intervalSeconds = intervalSeconds;
        ui = SynchronizationContext.Current;
    }

    public bool IsRunning(SessionInfo s) => Current is { Running: true } c && c.Session.Url == s.Url;

    static string SessionId(SessionInfo s) => s.Url.TrimEnd('/').Split('/').Last();
    public static string FolderFor(SessionInfo s) => Path.Combine(Root, SessionId(s));

    /// <summary>All captured screenshots for an app, newest first.</summary>
    public static List<string> ShotsFor(SessionInfo s, int max = 80)
    {
        var dir = FolderFor(s);
        if (!Directory.Exists(dir)) return new();
        return new DirectoryInfo(dir).EnumerateFiles("*.png", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTime).Take(max).Select(f => f.FullName).ToList();
    }

    public static void CleanupOld(int days = 30)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var d in new DirectoryInfo(Root).EnumerateDirectories("*", SearchOption.AllDirectories)
                         .Where(d => d.LastWriteTime < DateTime.Now.AddDays(-days)).ToList())
                try { d.Delete(true); } catch { }
        }
        catch { }
    }

    /// <summary>Starts a testing run. Returns null on success, else why not.</summary>
    public async Task<string?> StartAsync(SessionInfo s)
    {
        if (s.Package == null) return "Don't know this app's package name yet — run Check now.";
        Stop(); // one phone, one run at a time

        var err = await DeviceChecker.LaunchAsync(adbPath(), s.Package);
        if (err != null) return err;

        var run = new CaptureRun
        {
            Session = s,
            Folder = Path.Combine(FolderFor(s), DateTime.Now.ToString("yyyyMMdd-HHmmss")),
            Running = true
        };
        Directory.CreateDirectory(run.Folder);
        Current = run;
        Log.Write($"Testing started: {s.App} → {run.Folder}");
        Raise(Changed, run);
        _ = LoopAsync(run);
        return null;
    }

    public void Stop()
    {
        if (Current is not { Running: true } run) return;
        run.Running = false;
        run.Cts.Cancel();
        Log.Write($"Testing stopped: {run.Session.App}, {run.Count} screenshots");
        Raise(Changed, run);
    }

    /// <summary>Takes one screenshot right now (whatever is on screen) for this app.</summary>
    public async Task<(string? file, string? error)> CaptureNowAsync(SessionInfo s)
    {
        var bytes = await Task.Run(() => DeviceChecker.ScreencapAsync(adbPath()));
        if (bytes == null) return (null, "Couldn't capture with the clock showing — is the phone connected and unlocked?");
        var folder = IsRunning(s) ? Current!.Folder : Path.Combine(FolderFor(s), "manual");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"shot-{DateTime.Now:HHmmss-fff}.png");
        await File.WriteAllBytesAsync(file, bytes);
        if (IsRunning(s)) { Current!.Count++; Raise(Changed, Current); }
        return (file, null);
    }

    async Task LoopAsync(CaptureRun run)
    {
        byte[]? lastHash = null;
        int misses = 0;
        var token = run.Cts.Token;
        try
        {
            // Give the app a moment to open.
            await Task.Delay(TimeSpan.FromSeconds(4), token);
            while (!token.IsCancellationRequested)
            {
                if (DateTime.Now - run.Started > MaxRun)
                {
                    run.Problem = $"Stopped automatically after {MaxRun.TotalMinutes:0} minutes.";
                    break;
                }

                var fg = await Task.Run(() => DeviceChecker.ForegroundAsync(adbPath()), token);
                if (fg == null && ++misses >= 6)
                {
                    run.Problem = "Phone disconnected or locked — testing stopped.";
                    break;
                }
                if (fg != null) misses = 0;

                // Contains(): web apps (TWAs like CartaScore) show in a Chrome window but run in the app's own task.
                if (fg != null && fg.Contains(run.Session.Package!))
                {
                    var bytes = await Task.Run(() => DeviceChecker.ScreencapAsync(adbPath()), token);
                    if (bytes != null)
                    {
                        var hash = SHA1.HashData(bytes);
                        if (lastHash == null || !hash.AsSpan().SequenceEqual(lastHash))
                        {
                            lastHash = hash;
                            await File.WriteAllBytesAsync(Path.Combine(run.Folder, $"shot-{DateTime.Now:HHmmss}.png"), bytes, token);
                            run.Count++;
                            Raise(Changed, run);
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, intervalSeconds())), token);
            }
        }
        catch (OperationCanceledException) { return; } // stopped by you
        catch (Exception ex)
        {
            run.Problem = "Capture error: " + ex.Message;
            Log.Write("Capture loop failed: " + ex);
        }

        if (run.Running)
        {
            run.Running = false;
            Log.Write($"Testing ended automatically: {run.Session.App}, {run.Count} screenshots. {run.Problem}");
            Raise(Changed, run);
            Raise(EndedAutomatically, run);
        }
    }

    void Raise(Action<CaptureRun>? evt, CaptureRun run)
    {
        if (evt == null) return;
        if (ui != null) ui.Post(_ => evt(run), null);
        else evt(run);
    }
}
