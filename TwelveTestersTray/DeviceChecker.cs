using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TwelveTestersTray;

public class DeviceStatus
{
    public bool AdbFound { get; set; }
    public bool Connected { get; set; }
    public string? Model { get; set; }
    /// <summary>Why the phone can't be checked (not connected, unauthorised, …), if so.</summary>
    public string? Problem { get; set; }
    public HashSet<string> Installed { get; set; } = new();
    public Dictionary<string, DateTime> LastOpened { get; set; } = new();

    public string Name => string.IsNullOrEmpty(Model) ? "your phone" : Model!;

    public bool IsInstalled(string? pkg) => Connected && pkg != null && Installed.Contains(pkg);
    public bool IsMissing(string? pkg) => Connected && pkg != null && !Installed.Contains(pkg);
    public bool OpenedToday(string? pkg) =>
        pkg != null && LastOpened.TryGetValue(pkg, out var t) && t.Date == DateTime.Today;
}

/// <summary>Checks the USB-connected Android test phone with adb (read-only).</summary>
public static class DeviceChecker
{
    public static string? FindAdb(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured!);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));

        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } root)
                candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(pf86, "Android", "android-sdk", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(pf, "Android", "android-sdk", "platform-tools", "adb.exe"));
        candidates.Add(@"C:\platform-tools\adb.exe");
        candidates.Add(@"C:\Android\platform-tools\adb.exe");

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<DeviceStatus> CheckAsync(string? adbPathSetting, IEnumerable<string> packages)
    {
        var status = new DeviceStatus();
        var adb = FindAdb(adbPathSetting);
        if (adb == null)
        {
            status.Problem = "adb not found — install Android platform-tools to enable phone checks.";
            return status;
        }
        status.AdbFound = true;

        // Pick the first authorised device.
        var devices = (await RunAsync(adb, "devices"))
            .Split('\n').Skip(1)
            .Select(l => l.Trim().Split('\t'))
            .Where(p => p.Length == 2)
            .ToList();

        var ready = devices.FirstOrDefault(d => d[1] == "device");
        if (ready == null)
        {
            status.Problem = devices.Any(d => d[1] == "unauthorized")
                ? "Phone connected but USB debugging isn't allowed — unlock it and tap Allow."
                : devices.Any(d => d[1] == "offline")
                    ? "Phone shows as offline — reconnect the USB cable."
                    : "Test phone not connected by USB.";
            return status;
        }

        string s = $"-s {ready[0]}";
        status.Connected = true;
        status.Model = (await RunAsync(adb, $"{s} shell getprop ro.product.model")).Trim();

        var all = await RunAsync(adb, $"{s} shell pm list packages");
        foreach (Match m in Regex.Matches(all, @"^package:(\S+)", RegexOptions.Multiline))
            status.Installed.Add(m.Groups[1].Value);

        var wanted = packages.Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
        if (wanted.Count > 0)
        {
            // Last 24h of app-to-foreground events.
            var events = await RunAsync(adb,
                $"{s} shell \"dumpsys usagestats | grep -E 'type=(ACTIVITY_RESUMED|MOVE_TO_FOREGROUND) '\"", 30000);
            foreach (Match m in Regex.Matches(events, @"time=""([^""]+)""\s+type=\S+\s+package=(\S+)"))
            {
                var pkg = m.Groups[2].Value;
                if (!wanted.Contains(pkg)) continue;
                if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var when)) continue;
                if (!status.LastOpened.TryGetValue(pkg, out var prev) || when > prev)
                    status.LastOpened[pkg] = when;
            }
        }

        return status;
    }

    static async Task<(string? adb, string? serialArg, string? error)> ConnectAsync(string? adbPathSetting)
    {
        var adb = FindAdb(adbPathSetting);
        if (adb == null) return (null, null, "adb not found.");
        var devices = (await RunAsync(adb, "devices")).Split('\n').Skip(1)
            .Select(l => l.Trim().Split('\t')).Where(p => p.Length == 2 && p[1] == "device").ToList();
        if (devices.Count == 0) return (adb, null, "Test phone not connected by USB.");
        return (adb, $"-s {devices[0][0]}", null);
    }

    /// <summary>Package of the app currently on screen, or null.</summary>
    public static async Task<string?> ForegroundPackageAsync(string? adbPathSetting) =>
        (await ForegroundAsync(adbPathSetting))?.FirstOrDefault();

    /// <summary>True when <paramref name="package"/> is what's on screen — including web apps (TWAs)
    /// that run inside a Chrome Custom Tab, where the window belongs to Chrome but the task to the app.</summary>
    public static async Task<bool> IsOnScreenAsync(string? adbPathSetting, string package) =>
        (await ForegroundAsync(adbPathSetting))?.Contains(package) == true;

    /// <summary>
    /// Packages that "own" what's on screen: the focused window's package first, then the package of the
    /// task it lives in (differs for Trusted Web Activities / Custom Tabs). Null if the phone isn't reachable
    /// or nothing is focused (e.g. locked).
    /// </summary>
    public static async Task<List<string>?> ForegroundAsync(string? adbPathSetting)
    {
        var (adb, s, err) = await ConnectAsync(adbPathSetting);
        if (err != null) return null;
        var win = await RunAsync(adb!, $"{s} shell \"dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'\"");
        var m = Regex.Match(win, @"mCurrentFocus=Window\{\S+ \S+ ([A-Za-z0-9_.]+)/");
        if (!m.Success) m = Regex.Match(win, @"mFocusedApp=.*? ([A-Za-z0-9_.]+)/");
        if (!m.Success) return null;
        var result = new List<string> { m.Groups[1].Value };

        try
        {
            var act = await RunAsync(adb!, $"{s} shell \"dumpsys activity activities | grep -E 'ResumedActivity|\\* Task\\{{'\"");
            var r = Regex.Match(act, @"ResumedActivity:? ActivityRecord\{\S+ \S+ ([A-Za-z0-9_.]+)/\S+ t(\d+)");
            if (r.Success)
            {
                var task = Regex.Match(act, $@"\* Task\{{\S+ #{r.Groups[2].Value} [^\n]*?(?:A=(?:\d+:)?([A-Za-z0-9_.]+)|I=([A-Za-z0-9_.]+)/)");
                var owner = task.Success ? (task.Groups[1].Success ? task.Groups[1].Value : task.Groups[2].Value) : null;
                if (!string.IsNullOrEmpty(owner) && !result.Contains(owner)) result.Add(owner);
            }
        }
        catch { /* window package alone is still useful */ }
        return result;
    }

    /// <summary>
    /// PNG of the phone's screen with the status-bar clock showing (Twelve Testers checks it), or null if
    /// there's no phone or the clock couldn't be shown. Full-screen apps/games hide the status bar: then a
    /// short swipe down from the very top edge brings it back for a few seconds (over the app, without
    /// opening the notification panel) and the screenshot is taken while it's visible.
    /// </summary>
    public static async Task<byte[]?> ScreencapAsync(string? adbPathSetting)
    {
        var (adb, s, err) = await ConnectAsync(adbPathSetting);
        if (err != null) return null;

        var visible = await StatusBarVisibleAsync(adb!, s!);
        for (int attempt = 0; visible == false && attempt < 2; attempt++)
        {
            int x = await ScreenWidthAsync(adb!, s!) / 2;
            await RunAsync(adb!, $"{s} shell input swipe {x} 1 {x} 250 120");
            await Task.Delay(450);
            visible = await StatusBarVisibleAsync(adb!, s!);
        }
        if (visible == false)
        {
            Log.Write("Screenshot skipped: status bar (clock) couldn't be shown.");
            return null;
        }
        // visible == null: this Android version doesn't report it — capture anyway.
        return await RawScreencapAsync(adb!, s!);
    }

    /// <summary>True/false from the window manager's status-bar inset, or null if it isn't reported.</summary>
    static async Task<bool?> StatusBarVisibleAsync(string adb, string s)
    {
        var outp = await RunAsync(adb, $"{s} shell \"dumpsys window | grep -m1 'InsetsSource type=ITYPE_STATUS_BAR'\"");
        var m = Regex.Match(outp, @"visible=(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : null;
    }

    static int? screenWidth;
    static async Task<int> ScreenWidthAsync(string adb, string s)
    {
        if (screenWidth is int w) return w;
        var outp = await RunAsync(adb, $"{s} shell wm size");
        var ms = Regex.Matches(outp, @"(\d+)x(\d+)"); // last match = override size if any
        screenWidth = ms.Count > 0 ? int.Parse(ms[^1].Groups[1].Value) : 1080;
        return screenWidth.Value;
    }

    static async Task<byte[]?> RawScreencapAsync(string adb, string s)
    {
        var psi = new ProcessStartInfo(adb!, $"{s} exec-out screencap -p")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start adb.");
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(20000);
        try { await p.WaitForExitAsync(cts.Token); await copy; }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
        var bytes = ms.ToArray();
        // Must start with the PNG signature.
        return bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? bytes : null;
    }

    /// <summary>Quick check: installed package names, or null if the phone isn't reachable.</summary>
    public static async Task<HashSet<string>?> InstalledPackagesAsync(string? adbPathSetting)
    {
        var (adb, s, err) = await ConnectAsync(adbPathSetting);
        if (err != null) return null;
        var all = await RunAsync(adb!, $"{s} shell pm list packages");
        return Regex.Matches(all, @"^package:(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
    }

    /// <summary>
    /// Opens the app on the phone, waits until it's on screen, and returns a fresh PNG of it
    /// (the status-bar clock is what Twelve Testers checks), or an error.
    /// </summary>
    public static async Task<(byte[]? png, string? error)> CaptureAppAsync(string? adbPathSetting, string package)
    {
        var err = await LaunchAsync(adbPathSetting, package);
        if (err != null) return (null, err);
        List<string>? fg = null;
        bool onScreen = false;
        for (int i = 0; i < 12; i++) // up to ~12 s for slow-starting apps / splash screens / web apps
        {
            await Task.Delay(1000);
            fg = await ForegroundAsync(adbPathSetting);
            onScreen = fg?.Contains(package) == true;
            if (onScreen && i >= 2) break; // give it a couple of seconds past the splash
        }
        if (!onScreen)
            return (null, $"The app didn't come to the front on the phone (showing: {(fg == null ? "nothing — is it locked?" : string.Join(" / ", fg))}). Unlock the phone and try again.");
        var png = await ScreencapAsync(adbPathSetting);
        return png == null
            ? (null, "Couldn't take a screenshot with the clock showing. Make sure the phone is unlocked; if the app is " +
                     "full-screen, swipe down from the top edge on the phone so the clock shows, then try again.")
            : (png, null);
    }

    public static async Task<string?> AppVersionAsync(string? adbPathSetting, string package)
    {
        var (adb, s, err) = await ConnectAsync(adbPathSetting);
        if (err != null) return null;
        var outp = await RunAsync(adb!, $"{s} shell \"dumpsys package {package} | grep versionName\"");
        var m = Regex.Match(outp, @"versionName=(\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static async Task<string?> AndroidVersionAsync(string? adbPathSetting)
    {
        var (adb, s, err) = await ConnectAsync(adbPathSetting);
        if (err != null) return null;
        return (await RunAsync(adb!, $"{s} shell getprop ro.build.version.release")).Trim();
    }

    /// <summary>Opens the app's Play Store page on the phone (where tester feedback can be written).</summary>
    public static async Task<string?> OpenPlayPageAsync(string? adbPathSetting, string package)
    {
        var (adb, s, err) = await ConnectAsync(adbPathSetting);
        if (err != null) return err;
        await RunAsync(adb!, $"{s} shell input keyevent KEYCODE_WAKEUP");
        await RunAsync(adb!, $"{s} shell am start -a android.intent.action.VIEW -d market://details?id={package} -p com.android.vending");
        return null;
    }

    /// <summary>Opens an app on the connected phone. Returns null on success, else why not.</summary>
    public static async Task<string?> LaunchAsync(string? adbPathSetting, string package)
    {
        var (adb, s, err) = await ConnectAsync(adbPathSetting);
        if (err != null) return err;
        await RunAsync(adb!, $"{s} shell input keyevent KEYCODE_WAKEUP");
        var outp = await RunAsync(adb!, $"{s} shell monkey -p {package} -c android.intent.category.LAUNCHER 1");
        return outp.Contains("No activities found", StringComparison.OrdinalIgnoreCase)
            ? $"{package} isn't installed on the phone."
            : null;
    }

    static async Task<string> RunAsync(string exe, string args, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start adb.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            throw new TimeoutException($"adb {args} timed out");
        }
        return await stdout;
    }
}
