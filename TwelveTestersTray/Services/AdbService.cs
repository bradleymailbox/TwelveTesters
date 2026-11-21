using System.Globalization;
using System.Text.RegularExpressions;

namespace TwelveTestersTray.Services;

/// <summary>One configurable phone service. Discovery, process startup and parsing run off the UI thread.</summary>
public sealed class AdbService
{
    private string? configuredPath;
    private string? adbPath;
    public string? AdbPath => Volatile.Read(ref adbPath);
    public AdbService(string? configuredPath) => this.configuredPath = configuredPath;
    public void Configure(string? path)
    {
        Volatile.Write(ref configuredPath, path);
        Volatile.Write(ref adbPath, null);
    }

    public Task<string?> RefreshPathAsync(CancellationToken ct = default) => Task.Run(() => ResolvePath(), ct);

    private string? ResolvePath()
    {
        var configured = Volatile.Read(ref configuredPath);
        var found = SearchPaths(configured, Environment.GetEnvironmentVariable,
            Environment.GetFolderPath).FirstOrDefault(File.Exists);
        if (configured == Volatile.Read(ref configuredPath)) Volatile.Write(ref adbPath, found);
        return found;
    }

    // Keep this order aligned with App Deployer's AdbService.Locate.
    internal static IEnumerable<string> SearchPaths(string? configured, Func<string, string?> environment,
        Func<Environment.SpecialFolder, string> folder)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        foreach (var dir in (environment("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) yield return Path.Combine(dir.Trim('"'), "adb.exe");
        foreach (var name in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (environment(name) is { Length: > 0 } root) yield return Path.Combine(root, "platform-tools", "adb.exe");
        yield return Path.Combine(folder(Environment.SpecialFolder.ProgramFilesX86), "Android", "android-sdk", "platform-tools", "adb.exe");
        yield return Path.Combine(folder(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe");
        yield return Path.Combine(folder(Environment.SpecialFolder.ProgramFiles), "Android", "android-sdk", "platform-tools", "adb.exe");
        // Extra locations the old DeviceChecker searched; kept last so App Deployer's order wins.
        yield return @"C:\platform-tools\adb.exe";
        yield return @"C:\Android\platform-tools\adb.exe";
    }

    private sealed record Connection(string Exe, string Serial);

    private static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, int seconds = 20)
    {
        var result = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"ADB exited with code {result.ExitCode}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}");
        return result.Output;
    }

    private static Task<string> ShellAsync(Connection phone, CancellationToken ct, params string[] args) =>
        RunAsync(phone.Exe, new[] { "-s", phone.Serial, "shell" }.Concat(args), ct, 30);

    private static List<string[]> Devices(string output) => output.Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && parts[0] != "List" && parts[0] != "*").ToList();

    private async Task<Connection> ConnectAsync(CancellationToken ct)
    {
        var exe = ResolvePath() ?? throw new IOException("ADB not found. Set its location in Settings.");
        var devices = Devices(await RunAsync(exe, ["devices"], ct).ConfigureAwait(false));
        var ready = devices.FirstOrDefault(d => d[1] == "device");
        if (ready == null) throw new IOException(devices.Any(d => d[1] == "unauthorized")
            ? "Phone connected but USB debugging isn't allowed — unlock it and tap Allow."
            : devices.Any(d => d[1] == "offline") ? "Phone shows as offline — reconnect the USB cable."
            : "Test phone not connected by USB.");
        return new(exe, ready[0]);
    }

    public Task<DeviceStatus> CheckAsync(IEnumerable<string> packages, CancellationToken ct = default)
    {
        var wanted = packages.Where(p => !string.IsNullOrWhiteSpace(p)).ToHashSet();
        return Task.Run(async () =>
        {
            var status = new DeviceStatus();
            try
            {
                var phone = await ConnectAsync(ct).ConfigureAwait(false);
                status.AdbFound = status.Connected = true;
                status.Model = (await ShellAsync(phone, ct, "getprop", "ro.product.model").ConfigureAwait(false)).Trim();
                status.Installed = ParsePackages(await ShellAsync(phone, ct, "pm", "list", "packages").ConfigureAwait(false));
                if (wanted.Count > 0)
                {
                    var events = await ShellAsync(phone, ct, "dumpsys", "usagestats").ConfigureAwait(false);
                    foreach (Match match in Regex.Matches(events, "time=\"([^\"]+)\"\\s+type=(?:ACTIVITY_RESUMED|MOVE_TO_FOREGROUND)\\s+package=(\\S+)"))
                    {
                        var package = match.Groups[2].Value;
                        if (!wanted.Contains(package) || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)) continue;
                        if (!status.LastOpened.TryGetValue(package, out var previous) || when > previous) status.LastOpened[package] = when;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.AdbFound = AdbPath != null;
                status.Connected = false; // don't present a partial/failed read as a fresh phone snapshot
                status.Problem = ex.Message;
            }
            return status;
        }, ct);
    }

    private static HashSet<string> ParsePackages(string output) => Regex.Matches(output, @"^package:(\S+)", RegexOptions.Multiline)
        .Select(m => m.Groups[1].Value).ToHashSet();

    private Task<T?> ReadAsync<T>(Func<Connection, CancellationToken, Task<T?>> action, CancellationToken ct) where T : class =>
        Task.Run(async () =>
        {
            try { return await action(await ConnectAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { Log.Write("Phone read failed: " + ex.Message); return null; }
        }, ct);

    public Task<HashSet<string>?> InstalledPackagesAsync(CancellationToken ct = default) => ReadAsync<HashSet<string>>(async (phone, token) =>
        ParsePackages(await ShellAsync(phone, token, "pm", "list", "packages").ConfigureAwait(false)), ct);

    public Task<List<string>?> ForegroundAsync(CancellationToken ct = default) => ReadAsync<List<string>>(ForegroundCoreAsync, ct);
    public async Task<string?> ForegroundPackageAsync(CancellationToken ct = default) =>
        (await ForegroundAsync(ct).ConfigureAwait(false))?.FirstOrDefault();
    public async Task<bool> IsOnScreenAsync(string package, CancellationToken ct = default) =>
        (await ForegroundAsync(ct).ConfigureAwait(false))?.Contains(package) == true;

    private static async Task<List<string>?> ForegroundCoreAsync(Connection phone, CancellationToken ct)
    {
        var win = await ShellAsync(phone, ct, "dumpsys", "window").ConfigureAwait(false);
        var match = Regex.Match(win, @"mCurrentFocus=Window\{\S+ \S+ ([A-Za-z0-9_.]+)/");
        if (!match.Success) match = Regex.Match(win, @"mFocusedApp=.*? ([A-Za-z0-9_.]+)/");
        if (!match.Success) return null;
        var result = new List<string> { match.Groups[1].Value };
        try
        {
            var activity = await ShellAsync(phone, ct, "dumpsys", "activity", "activities").ConfigureAwait(false);
            var resumed = Regex.Match(activity, @"ResumedActivity:? ActivityRecord\{\S+ \S+ ([A-Za-z0-9_.]+)/\S+ t(\d+)");
            if (resumed.Success)
            {
                var task = Regex.Match(activity, $@"\* Task\{{\S+ #{resumed.Groups[2].Value} [^\n]*?(?:A=(?:\d+:)?([A-Za-z0-9_.]+)|I=([A-Za-z0-9_.]+)/)");
                var owner = task.Success ? (task.Groups[1].Success ? task.Groups[1].Value : task.Groups[2].Value) : null;
                if (!string.IsNullOrEmpty(owner) && !result.Contains(owner)) result.Add(owner);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* Window package alone is still useful. */ }
        return result;
    }

    public Task<byte[]?> ScreencapAsync(CancellationToken ct = default) => ReadAsync<byte[]>(ScreencapCoreAsync, ct);
    private static async Task<byte[]?> ScreencapCoreAsync(Connection phone, CancellationToken ct)
    {
        async Task<bool?> ClockVisible()
        {
            var output = await ShellAsync(phone, ct, "dumpsys", "window").ConfigureAwait(false);
            var match = Regex.Match(output, @"InsetsSource type=ITYPE_STATUS_BAR[^\r\n]*visible=(true|false)");
            return match.Success ? match.Groups[1].Value == "true" : null;
        }
        var visible = await ClockVisible().ConfigureAwait(false);
        for (var attempt = 0; visible == false && attempt < 2; attempt++)
        {
            var dimensions = Regex.Matches(await ShellAsync(phone, ct, "wm", "size").ConfigureAwait(false), @"(\d+)x(\d+)");
            var x = dimensions.Count > 0 ? int.Parse(dimensions[^1].Groups[1].Value) / 2 : 540;
            await ShellAsync(phone, ct, "input", "swipe", x.ToString(CultureInfo.InvariantCulture), "1", x.ToString(CultureInfo.InvariantCulture), "250", "120").ConfigureAwait(false);
            await Task.Delay(450, ct).ConfigureAwait(false);
            visible = await ClockVisible().ConfigureAwait(false);
        }
        if (visible == false)
        {
            Log.Write("Screenshot skipped: status bar (clock) couldn't be shown.");
            return null;
        }
        // visible == null: this Android version doesn't report it, so capture anyway.
        var result = await ProcessRunner.RunBinaryAsync(phone.Exe, ["-s", phone.Serial, "exec-out", "screencap", "-p"], ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("Screenshot failed: " + result.Error.Trim());
        return result.Output.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? result.Output : null;
    }

    private static void ValidatePackage(string package)
    {
        if (!Regex.IsMatch(package, @"\A[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+\z"))
            throw new ArgumentException("The app's package name is missing or invalid.");
    }

    private Task<string?> ActionAsync(string package, Func<Connection, CancellationToken, Task<string?>> action, CancellationToken ct) =>
        Task.Run(async () =>
        {
            try { ValidatePackage(package); return await action(await ConnectAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return ex.Message; }
        }, ct);

    public Task<string?> LaunchAsync(string package, CancellationToken ct = default) =>
        ActionAsync(package, (phone, token) => LaunchCoreAsync(phone, package, token), ct);
    private static async Task<string?> LaunchCoreAsync(Connection phone, string package, CancellationToken ct)
    {
        await ShellAsync(phone, ct, "input", "keyevent", "KEYCODE_WAKEUP").ConfigureAwait(false);
        // monkey exits non-zero (and writes to stderr) when the package has no launcher activity, so read
        // both streams before treating the exit code as a failure.
        var result = await ProcessRunner.RunAsync(phone.Exe,
            ["-s", phone.Serial, "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1"],
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if ((result.Output + result.Error).Contains("No activities found", StringComparison.OrdinalIgnoreCase))
            return $"{package} isn't installed on the phone.";
        if (result.ExitCode != 0)
            throw new IOException($"Couldn't open {package}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}");
        return null;
    }

    public Task<string?> OpenPlayPageAsync(string package, CancellationToken ct = default) => ActionAsync(package, async (phone, token) =>
    {
        await ShellAsync(phone, token, "input", "keyevent", "KEYCODE_WAKEUP").ConfigureAwait(false);
        await ShellAsync(phone, token, "am", "start", "-a", "android.intent.action.VIEW", "-d", $"market://details?id={package}", "-p", "com.android.vending").ConfigureAwait(false);
        return null;
    }, ct);

    public Task<string?> AndroidVersionAsync(CancellationToken ct = default) => ReadAsync<string>(async (phone, token) =>
        (await ShellAsync(phone, token, "getprop", "ro.build.version.release").ConfigureAwait(false)).Trim(), ct);
    public Task<string?> AppVersionAsync(string package, CancellationToken ct = default) => ReadAsync<string>(async (phone, token) =>
    {
        ValidatePackage(package);
        var match = Regex.Match(await ShellAsync(phone, token, "dumpsys", "package", package).ConfigureAwait(false), @"versionName=(\S+)");
        return match.Success ? match.Groups[1].Value : null;
    }, ct);

    public Task<(byte[]? png, string? error)> CaptureAppAsync(string package, CancellationToken ct = default) => Task.Run(async () =>
    {
        try
        {
            ValidatePackage(package);
            var phone = await ConnectAsync(ct).ConfigureAwait(false);
            var error = await LaunchCoreAsync(phone, package, ct).ConfigureAwait(false);
            if (error != null) return ((byte[]?)null, error);
            List<string>? foreground = null;
            var onScreen = false;
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                foreground = await ForegroundCoreAsync(phone, ct).ConfigureAwait(false);
                onScreen = foreground?.Contains(package) == true;
                if (onScreen && i >= 2) break;
            }
            if (!onScreen) return ((byte[]?)null, "The app didn't come to the front on the phone (showing: " +
                (foreground == null ? "nothing — is it locked?" : string.Join(" / ", foreground)) + "). Unlock the phone and try again.");
            var png = await ScreencapCoreAsync(phone, ct).ConfigureAwait(false);
            return (png, png == null ? "Couldn't capture with the clock showing. Unlock the phone and swipe down from the top edge, then retry." : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ((byte[]?)null, ex.Message); }
    }, ct);
}
