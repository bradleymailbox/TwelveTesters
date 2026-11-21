using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using System.Windows.Interop;

namespace TwelveTestersTray.Services;

/// <summary>Site automation hosted by a controller on a never-shown native window.</summary>
public sealed class SiteBrowser : IDisposable
{
    public const string Base = "https://www.twelvetesters.com";
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    readonly SemaphoreSlim busy = new(1, 1);
    private Task<CoreWebView2Environment>? environmentTask;
    public Task<CoreWebView2Environment> EnvironmentTask => environmentTask ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Dir, "WebView2"));
    private HwndSource? host;
    private CoreWebView2Controller? controller;
    private CoreWebView2 core = null!;
    private Task? initialization;
    private bool disposed;
    public bool IsSignInOpen { get; set; }
    public Task InitAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (initialization?.IsFaulted == true || initialization?.IsCanceled == true)
        {
            controller?.Close();
            controller = null;
            host?.Dispose();
            host = null;
            if (environmentTask?.IsFaulted == true || environmentTask?.IsCanceled == true) environmentTask = null;
            initialization = null;
        }
        return initialization ??= InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        var environment = await EnvironmentTask;
        if (disposed) throw new ObjectDisposedException(nameof(SiteBrowser));
        // Must count as visible or WebView2 suspends rendering and the client-side site never fills in (0 sessions).
        // Like the old form: a real popup parked far off-screen, hidden from the taskbar, never activated.
        host = new HwndSource(new HwndSourceParameters("Twelve Testers site browser")
        {
            Width = 1500, Height = 950, PositionX = -30000, PositionY = -30000,
            WindowStyle = unchecked((int)0x90000000),           // WS_POPUP | WS_VISIBLE
            ExtendedWindowStyle = 0x00000080 | 0x08000000        // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        });
        var created = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
        if (disposed) { created.Close(); throw new ObjectDisposedException(nameof(SiteBrowser)); }
        controller = created;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1500, 950);
        controller.IsVisible = true;
        core = controller.CoreWebView2;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.ScriptDialogOpening += (_, e) => { if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload) e.Accept(); };
    }
    public void Dispose()
    {
        disposed = true;
        controller?.Close();
        host?.Dispose();
    }
    // ---------------------------------------------------------------- scraping

    /// <param name="getLogin">Returns saved credentials to auto-sign-in with, or null to skip.</param>
    /// <param name="packageCache">Session URL → package; read and filled in here.</param>
    public async Task<Snapshot> ScrapeAsync(Func<(string Email, string Password)?> getLogin,
                                            Dictionary<string, string> packageCache)
    {
        await InitAsync();
        var snap = new Snapshot { At = DateTime.Now };
        await busy.WaitAsync();
        try
        {
            var dash = await LoadDashboardAsync();
            if (dash == null || !dash.LoggedIn)
            {
                var creds = getLogin();
                if (creds == null) return snap;

                snap.LoginError = await LoginAsync(creds.Value.Email, creds.Value.Password);
                if (snap.LoginError != null) return snap;

                dash = await LoadDashboardAsync();
                if (dash == null || !dash.LoggedIn)
                {
                    Log.Write("Login: accepted, but /dashboard still not signed in. URL now: " + core.Source);
                    snap.LoginError = "Logged in, but the dashboard still asks to sign in (email verification needed?).";
                    return snap;
                }
                Log.Write("Login: success, dashboard loaded");
            }

            snap.SignedIn = true;
            snap.Credits = dash.Credits;
            snap.DueToday = dash.DueToday ?? 0;
            snap.FeedbackIn = dash.FeedbackIn ?? 0;

            if (await LoadAsync("/dashboard/sessions") == "ready")
                snap.Sessions = await EvalAsync<List<SessionInfo>>(SessionsJs) ?? new();

            // Work out which Play package each session is for (needed for phone checks).
            foreach (var x in snap.Sessions)
            {
                if (packageCache.TryGetValue(x.Url, out var cached)) { x.Package = cached; continue; }
                if (!Uri.TryCreate(x.Url, UriKind.Absolute, out var u)) continue;
                if (await LoadAsync(u.AbsolutePath) != "ready") continue;
                x.Package = await EvalAsync<string?>(PackageJs);
                if (!string.IsNullOrEmpty(x.Package)) packageCache[x.Url] = x.Package!;
                Log.Write($"Session {x.App}: package {x.Package ?? "(not found)"}");
            }

            if (await LoadAsync("/dashboard/requests") == "ready")
                snap.Requests = await EvalAsync<List<RequestInfo>>(RequestsJs) ?? new();

            if (await LoadAsync("/dashboard/check-in") == "ready")
            {
                var list = await EvalAsync<CheckinList>(CheckinListJs);
                snap.SiteCheckinDue = list?.Due.ToHashSet() ?? new();
                snap.SiteCheckinDone = list?.Done.ToHashSet() ?? new();
                snap.CheckinsDue = list != null && (list.Due.Count + list.Done.Count) > 0
                    ? list.Due.Count
                    : await EvalAsync<int?>(CheckinJs) ?? 0;
            }

            return snap;
        }
        finally
        {
            // Park on a blank page so the site isn't kept open (and polling) in the background.
            if (!IsSignInOpen) core?.Navigate("about:blank");
            busy.Release();
        }
    }

    // ---------------------------------------------------------------- feedback

    /// <summary>
    /// Fills in and submits the site's "submit report" form for one app, including screenshots.
    /// Returns null on success, else an error message.
    /// </summary>
    public async Task<string?> SubmitFeedbackAsync(SessionInfo session, FeedbackDraft d)
    {
        await InitAsync();
        if (IsSignInOpen) return "Close the sign-in window first.";
        await busy.WaitAsync();
        try
        {
            var path = new Uri(session.FeedbackUrl).AbsolutePath;
            if (await LoadAsync(path) == "login") return "Not signed in to Twelve Testers.";

            int before = await EvalAsync<int>(ReportCountJs);
            Log.Write($"Feedback: submitting to {session.App} ({d.Files.Count} screenshots), reports before: {before}");

            var files = d.Files.Select(f => new
            {
                name = Path.GetFileName(f),
                type = Path.GetExtension(f).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png"
                },
                b64 = Convert.ToBase64String(File.ReadAllBytes(f))
            }).ToList();
            var data = JsonSerializer.Serialize(new
            {
                type = d.Type, severity = d.Severity, title = d.Title, description = d.Description, files
            });

            await core.ExecuteScriptAsync(FeedbackFillJs.Replace("__DATA__", data));

            ApiLoginResult? res = null; // same {ok, message} shape
            for (int i = 0; i < 80 && res == null; i++)
            {
                await Task.Delay(250);
                res = await EvalAsync<ApiLoginResult>("window.__ttFb ? { status: 0, ok: window.__ttFb.ok, message: window.__ttFb.msg } : null");
            }
            if (res == null) return "The feedback form didn't respond.";
            if (!res.Ok) { Log.Write("Feedback: " + res.Message); return res.Message; }

            // Wait for the site to save it (screenshots upload on save).
            for (int i = 0; i < 90; i++)
            {
                await Task.Delay(1000);
                int now = await EvalAsync<int>(ReportCountJs);
                if (now > before)
                {
                    Log.Write($"Feedback: saved ({now} reports now)");
                    return null;
                }
                var err = await EvalAsync<string>(FormErrorJs);
                if (!string.IsNullOrWhiteSpace(err))
                {
                    Log.Write("Feedback: site error: " + err);
                    return "The site said: " + err;
                }
            }
            return "Timed out waiting for the report to save — check the app's feedback page before resending.";
        }
        finally
        {
            if (!IsSignInOpen) core?.Navigate("about:blank");
            busy.Release();
        }
    }

    // ------------------------------------------------------- install check-in

    /// <summary>True between a successful "Check screenshot" and Confirm/Cancel (the browser is held).</summary>
    public bool CheckinHeld { get; private set; }

    /// <summary>
    /// Day 1 install check-in, part 1: on the session page press "I joined the test" (only if
    /// <paramref name="pressJoined"/>), then "I installed it — upload screenshot", attach the screenshot
    /// and press "Check screenshot". Returns the site's verdict. Nothing is final until
    /// <see cref="ConfirmInstallCheckinAsync"/>; call <see cref="CancelInstallCheckin"/> otherwise.
    /// </summary>
    public async Task<InstallCheckResult> BeginInstallCheckinAsync(SessionInfo session, string file,
                                                                   DateTime capturedAt, bool pressJoined)
    {
        await InitAsync();
        if (IsSignInOpen) return new() { Message = "Close the sign-in window first." };
        if (CheckinHeld) CancelInstallCheckin();
        await busy.WaitAsync();
        bool keep = false;
        try
        {
            if (await LoadAsync(new Uri(session.Url).AbsolutePath) == "login")
                return new() { Message = "Not signed in to Twelve Testers." };

            Log.Write($"Install check-in: {session.App}, file {Path.GetFileName(file)} captured {capturedAt:HH:mm:ss}, pressJoined={pressJoined}");
            var data = JsonSerializer.Serialize(new
            {
                name = Path.GetFileName(file),
                type = Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png",
                b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file)),
                // The site's freshness check reads the file's timestamp: use the real capture time.
                lastModified = new DateTimeOffset(capturedAt).ToUnixTimeMilliseconds(),
                pressJoined
            });
            await core.ExecuteScriptAsync(InstallCheckinJs.Replace("__DATA__", data));

            InstallCheckResult? res = null;
            for (int i = 0; i < 360 && res == null; i++) // up to 90 s (upload + server-side check)
            {
                await Task.Delay(250);
                res = await EvalAsync<InstallCheckResult>("window.__ttIc || null");
            }
            res ??= new() { Message = "The session page didn't respond in time." };
            Log.Write($"Install check-in: ok={res.Ok} stage={res.Stage} confidence={res.Confidence} canConfirm={res.CanConfirm} msg={res.Message}");
            keep = res.CanConfirm;
            return res;
        }
        catch (Exception ex)
        {
            Log.Write("Install check-in failed: " + ex);
            return new() { Message = ex.Message };
        }
        finally
        {
            if (keep) CheckinHeld = true; // hold the page (and the browser) until Confirm/Cancel
            else
            {
                if (!IsSignInOpen) core?.Navigate("about:blank");
                busy.Release();
            }
        }
    }

    /// <summary>Set while a held check-in is a daily one (confirmation is verified on the check-in list).</summary>
    string? pendingDailyId;

    /// <summary>
    /// Daily check-in, part 1: on /dashboard/check-in/&lt;id&gt; press "All good — screenshot check-in"
    /// (this starts the site's 10-minute timer), THEN run <paramref name="capture"/> so the screenshot is
    /// taken inside that window, upload it and return the site's verdict. Finish with
    /// <see cref="ConfirmInstallCheckinAsync"/> or <see cref="CancelInstallCheckin"/>.
    /// </summary>
    public async Task<InstallCheckResult> BeginDailyCheckinAsync(SessionInfo session,
        Func<Task<(string? File, DateTime At, string? Error)>> capture)
    {
        await InitAsync();
        if (IsSignInOpen) return new() { Message = "Close the sign-in window first." };
        if (CheckinHeld) CancelInstallCheckin();
        await busy.WaitAsync();
        bool keep = false;
        try
        {
            if (await DailyStateAsync(session.Id) == "done")
                return new() { Stage = "done", Message = "Already checked in on the site today." };

            if (await LoadAsync("/dashboard/check-in/" + session.Id) == "login")
                return new() { Message = "Not signed in to Twelve Testers." };

            var start = await EvalAsync<string>(StartDailyJs);
            Log.Write($"Daily check-in: {session.App}, start={start}");
            if (start == "no-button")
                return new() { Message = "Couldn't find \"All good — screenshot check-in\" on the check-in page (the site may have changed)." };

            bool ready = false;
            for (int i = 0; i < 40 && !ready; i++)
            {
                await Task.Delay(250);
                ready = await EvalAsync<bool>("!!document.querySelector('main input[type=\"file\"]')");
            }
            if (!ready) return new() { Message = "The screenshot upload didn't appear after pressing \"All good\"." };

            var (file, at, err) = await capture(); // the 10-minute timer is running now
            if (err != null || file == null) return new() { Message = err ?? "No screenshot was taken." };

            var data = JsonSerializer.Serialize(new
            {
                name = Path.GetFileName(file),
                type = Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png",
                b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file)),
                lastModified = new DateTimeOffset(at).ToUnixTimeMilliseconds()
            });
            await core.ExecuteScriptAsync(DailyUploadJs.Replace("__DATA__", data));

            InstallCheckResult? res = null;
            for (int i = 0; i < 360 && res == null; i++)
            {
                await Task.Delay(250);
                res = await EvalAsync<InstallCheckResult>("window.__ttDc || null");
            }
            res ??= new() { Message = "The check-in page didn't respond in time." };

            if (res.Ok && res.Stage == "saved")
            {
                // Daily check-ins save straight away (no confirm step). The list can lag a few seconds
                // behind, so check it a few times before calling it a failure.
                await Task.Delay(1500);
                for (int i = 0; i < 6 && res.Stage != "done"; i++)
                {
                    if (await DailyStateAsync(session.Id) == "done") res.Stage = "done";
                    else await Task.Delay(2000);
                }
                if (res.Stage != "done")
                {
                    res.Ok = false;
                    res.Message = "The site didn't offer \"Confirm check-in\" and the check-in isn't showing as done yet — open the check-in page to look.";
                }
            }
            Log.Write($"Daily check-in: ok={res.Ok} stage={res.Stage} confidence={res.Confidence} canConfirm={res.CanConfirm} msg={res.Message}");
            keep = res.CanConfirm;
            if (keep) pendingDailyId = session.Id;
            return res;
        }
        catch (Exception ex)
        {
            Log.Write("Daily check-in failed: " + ex);
            return new() { Message = ex.Message };
        }
        finally
        {
            if (keep) CheckinHeld = true;
            else
            {
                if (!IsSignInOpen) core?.Navigate("about:blank");
                busy.Release();
            }
        }
    }

    /// <summary>"done", "due" or "missing" for one session on the check-in list (navigates there).</summary>
    async Task<string> DailyStateAsync(string sessionId)
    {
        if (await LoadAsync("/dashboard/check-in") != "ready") return "missing";
        return await EvalAsync<string>(CheckinRowJs.Replace("__ID__", JsonSerializer.Serialize(sessionId))) ?? "missing";
    }

    /// <summary>Part 2: press "Confirm check-in" and wait for "Done today". Null on success, else an error.</summary>
    public async Task<string?> ConfirmInstallCheckinAsync()
    {
        await InitAsync();
        if (!CheckinHeld) return "Nothing to confirm — run the screenshot check again.";
        try
        {
            await core.ExecuteScriptAsync(ConfirmCheckinJs);
            if (pendingDailyId != null)
            {
                await Task.Delay(2500);
                var err = await EvalAsync<string>(CheckinStateJs);
                for (int i = 0; i < 4; i++)
                {
                    if (await DailyStateAsync(pendingDailyId) == "done")
                    {
                        Log.Write("Daily check-in: confirmed (shows as done on the check-in list)");
                        return null;
                    }
                    await Task.Delay(2000);
                }
                return err != null && err.StartsWith("error:")
                    ? "The site said: " + err[6..]
                    : "The check-in isn't showing as done on the list yet — open Daily check-ins to look before trying again.";
            }
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                var state = await EvalAsync<string>(CheckinStateJs);
                if (state == "done") { Log.Write("Install check-in: confirmed (Done today)"); return null; }
                if (state != null && state.StartsWith("error:")) return "The site said: " + state[6..];
            }
            return "Didn't see \"Done today\" — open the session page to check before trying again.";
        }
        catch (Exception ex) { return ex.Message; }
        finally { ReleaseCheckin(); }
    }

    /// <summary>Leave without confirming (the unconfirmed upload is simply discarded).</summary>
    public void CancelInstallCheckin()
    {
        if (!CheckinHeld) return;
        Log.Write("Install check-in: cancelled before confirming");
        ReleaseCheckin();
    }

    void ReleaseCheckin()
    {
        if (!CheckinHeld) return;
        CheckinHeld = false;
        pendingDailyId = null;
        if (!IsSignInOpen) core?.Navigate("about:blank");
        busy.Release();
    }

    async Task<DashboardInfo?> LoadDashboardAsync()
    {
        if (await LoadAsync("/dashboard") == "login") return null;
        return await EvalAsync<DashboardInfo>(DashboardJs);
    }

    /// <summary>
    /// Signs in the same way the site's own login page does: POST JSON {email, password}
    /// to /api/auth/login from the site's origin so the session cookie is stored.
    /// Falls back to filling in the form if that endpoint isn't there.
    /// Returns null on success, else an error message.
    /// </summary>
    async Task<string?> LoginAsync(string email, string password)
    {
        await NavigateAsync("/login");
        Log.Write("Login: posting to /api/auth/login");

        var start = ApiLoginJs
            .Replace("__EMAIL__", JsonSerializer.Serialize(email))
            .Replace("__PASS__", JsonSerializer.Serialize(password));
        await core.ExecuteScriptAsync(start);

        ApiLoginResult? res = null;
        for (int i = 0; i < 60 && res == null; i++)
        {
            await Task.Delay(250);
            res = await EvalAsync<ApiLoginResult>("window.__ttLogin || null");
        }

        if (res == null)
        {
            Log.Write("Login: no response from /api/auth/login after 15s");
            return "No response from the Twelve Testers login (timed out).";
        }

        Log.Write($"Login: HTTP {res.Status}, message: {res.Message}");
        if (res.Ok) return null;
        if (res.Status == 404 || res.Status == 405) return await LoginViaFormAsync(email, password);
        if (res.Status == 0) return "Couldn't reach Twelve Testers: " + res.Message;

        return string.IsNullOrWhiteSpace(res.Message)
            ? $"Login rejected (HTTP {res.Status}) — check your email and password."
            : $"Login rejected: {res.Message}";
    }

    async Task<string?> LoginViaFormAsync(string email, string password)
    {
        Log.Write("Login: falling back to filling in the form");
        await NavigateAsync("/login");

        bool formFound = false;
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(250);
            if (await EvalAsync<bool>("!!document.querySelector('#auth-email') && !!document.querySelector('#auth-password')"))
            {
                formFound = true;
                break;
            }
            if ((await EvalAsync<string>("location.pathname") ?? "").StartsWith("/dashboard"))
                return null; // already signed in
        }
        if (!formFound) return "Couldn't find the login form (the site may have changed).";

        // JSON-encode the values so any character in the password is passed safely.
        var js = FillLoginJs
            .Replace("__EMAIL__", JsonSerializer.Serialize(email))
            .Replace("__PASS__", JsonSerializer.Serialize(password));
        var result = await EvalAsync<string>(js);
        if (result != "submitted") return $"Couldn't submit the login form ({result}).";

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var path = await EvalAsync<string>("location.pathname") ?? "";
            if (path != "/login") return null;
        }

        var msg = await EvalAsync<string>(LoginErrorJs);
        return string.IsNullOrWhiteSpace(msg) ? "Login didn't go through — check your email and password." : msg;
    }

    async Task NavigateAsync(string path)
    {
        var core = this.core ?? throw new InvalidOperationException("Browser not initialised.");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Base + path;
        ulong? myId = null;
        // Only react to *this* navigation: a previous one (e.g. parking on about:blank) can still complete
        // — as cancelled — after we start, which used to be mistaken for "couldn't load (offline?)".
        void Starting(object? s, CoreWebView2NavigationStartingEventArgs e)
        {
            if (myId == null && e.Uri.StartsWith(target, StringComparison.OrdinalIgnoreCase)) myId = e.NavigationId;
        }
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (myId == e.NavigationId) tcs.TrySetResult(e.IsSuccess);
        }

        core.NavigationStarting += Starting;
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(target);
            if (await Task.WhenAny(tcs.Task, Task.Delay(30000)) != tcs.Task)
                throw new TimeoutException($"Timed out loading {path}");
            if (!tcs.Task.Result)
                throw new InvalidOperationException($"Couldn't load {path} (offline?)");
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Handler;
        }
    }

    async Task<string> LoadAsync(string path)
    {
        await NavigateAsync(path);
        return await WaitForRenderAsync();
    }

    async Task<string> WaitForRenderAsync()
    {
        // The site renders client-side: wait until the page text stops changing.
        string prev = "";
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(700);
            var state = await EvalAsync<string>(ReadyJs) ?? "wait";
            if (state == "login") return "login";
            if (state.StartsWith("ready:"))
            {
                if (state == prev) return "ready";
                prev = state;
            }
            else prev = "";
        }
        return "ready"; // best effort
    }

    async Task<T?> EvalAsync<T>(string js)
    {
        var json = await core.ExecuteScriptAsync(js);
        return JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    // ------------------------------------------------------------ page scripts

    const string ReadyJs = """
        (() => {
          if (!location.pathname.startsWith('/dashboard')) return 'login';
          const m = document.querySelector('main');
          if (!m) return 'wait';
          const t = m.innerText || '';
          if (/Loading/.test(t)) return 'wait';
          return 'ready:' + t.length;
        })()
        """;

    // ExecuteScriptAsync doesn't await promises, so the result is parked on window and polled.
    const string ApiLoginJs = """
        (() => {
          window.__ttLogin = null;
          fetch('/api/auth/login', {
            method: 'POST',
            credentials: 'include',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ email: __EMAIL__, password: __PASS__ })
          }).then(async r => {
            let msg = '';
            try {
              const t = await r.text();
              try { const j = JSON.parse(t); msg = j.error || j.message || j.detail || ''; }
              catch { msg = t.slice(0, 200); }
            } catch {}
            window.__ttLogin = { status: r.status, ok: r.ok, message: String(msg || '') };
          }).catch(e => { window.__ttLogin = { status: 0, ok: false, message: String(e) }; });
          return 'started';
        })()
        """;

    // React-controlled inputs: set the value via the native setter and fire input events.
    const string FillLoginJs = """
        (() => {
          const em = document.querySelector('#auth-email');
          const pw = document.querySelector('#auth-password');
          if (!em || !pw) return 'no-form';
          const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
          const set = (el, v) => {
            el.focus();
            setter.call(el, v);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
          };
          set(em, __EMAIL__);
          set(pw, __PASS__);
          const form = em.closest('form');
          const btn = [...(form || document).querySelectorAll('button')].find(b => /^\s*Log in\s*$/i.test(b.innerText));
          if (btn) { btn.click(); return 'submitted'; }
          if (form) { form.requestSubmit(); return 'submitted'; }
          return 'no-button';
        })()
        """;

    const string LoginErrorJs = """
        (() => {
          const a = document.querySelector('[role="alert"], .auth-error, .error');
          if (a && a.innerText.trim()) return a.innerText.trim().slice(0, 200);
          const t = (document.querySelector('main') || document.body).innerText;
          const m = t.match(/[^\n]*(invalid|incorrect|wrong|not found|too many|verify|failed)[^\n]*/i);
          return m ? m[0].trim().slice(0, 200) : '';
        })()
        """;

    const string DashboardJs = """
        (() => {
          const t = (document.querySelector('main') || document.body).innerText;
          const num = re => { const m = t.match(re); return m ? parseInt(m[1], 10) : null; };
          return {
            credits:    num(/(\d+)\s*\n\s*credits/i),
            dueToday:   num(/(\d+)\s+still due today/i),
            feedbackIn: num(/(\d+)\s*\n\s*Feedback in/i),
            requestsIn: num(/(\d+)\s*\n\s*Requests in/i),
            // textContent (not innerText) so it works even when the menu is collapsed/hidden.
            loggedIn:   location.pathname.startsWith('/dashboard') &&
                        (/Log out/i.test(document.body.textContent) ||
                         !!document.querySelector('a[href="/dashboard/sessions"]'))
          };
        })()
        """;

    const string SessionsJs = """
        (() => [...document.querySelectorAll('main a[href*="/dashboard/sessions/"]')].map(a => {
          const l = a.innerText.split('\n').map(s => s.trim()).filter(Boolean);
          const yi = l.findIndex(x => /^You test$/i.test(x));
          const ti = l.findIndex((x, i) => i > yi + 2 && / tests$/i.test(x));
          return {
            url: a.href,
            partner: (l[0] || '').replace(/^Swap with\s*/i, ''),
            daysLeft: l.find(x => /days? left|ended|complete/i.test(x)) || '',
            state: l.find(x => /^[A-Z]{4,}$/.test(x)) || '',
            app: yi >= 0 ? (l[yi + 1] || '') : '',
            myStatus: yi >= 0 ? (l[yi + 2] || '') : '',
            partnerStatus: ti >= 0 ? (l[ti + 2] || '') : ''
          };
        }))()
        """;

    const string RequestsJs = """
        (() => [...document.querySelectorAll('main button')]
          .filter(b => /Accept/i.test(b.innerText))
          .map(b => {
            let c = b;
            while (c && c !== document.body && !/THEY OFFERED/i.test(c.innerText)) c = c.parentElement;
            const t = c ? c.innerText : '';
            const l = t.split('\n').map(s => s.trim()).filter(Boolean);
            const oi = l.findIndex(x => /^THEY OFFERED$/i.test(x));
            const fm = t.match(/From\s+(.+?)\s+·/);
            return { from: fm ? fm[1] : '', theirApp: oi >= 0 ? (l[oi + 1] || '') : '' };
          }))()
        """;

    const string PackageJs = """
        (() => {
          const re = /play\.google\.com\/(?:store\/apps\/details\?id=|apps\/testing\/)([A-Za-z0-9_.]+)/;
          const main = document.querySelector('main') || document.body;
          const m = main.innerText.match(re);
          if (m) return m[1];
          for (const a of main.querySelectorAll('a[href]')) {
            const h = a.getAttribute('href').match(re);
            if (h) return h[1];
          }
          return null;
        })()
        """;

    const string ReportCountJs = """
        (() => {
          const m = (document.querySelector('main') || document.body).innerText.match(/(\d+)\s*REPORTS?\b/i);
          return m ? parseInt(m[1], 10) : -1;
        })()
        """;

    const string FormErrorJs = """
        (() => {
          const a = document.querySelector('main [role="alert"], main .error, main [class*="error"]');
          return a && a.innerText.trim() ? a.innerText.trim().slice(0, 200) : '';
        })()
        """;

    // Async work; result parked on window.__ttFb and polled (ExecuteScriptAsync doesn't await promises).
    const string FeedbackFillJs = """
        (() => {
          window.__ttFb = null;
          const D = __DATA__;
          (async () => {
            const wait = ms => new Promise(r => setTimeout(r, ms));
            const m = document.querySelector('main');
            const setVal = (el, v) => {
              let p = Object.getPrototypeOf(el), d;
              while (p && !(d = Object.getOwnPropertyDescriptor(p, 'value'))) p = Object.getPrototypeOf(p);
              d.set.call(el, v);
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            const sels = m.querySelectorAll('select');
            if (sels.length < 2) { window.__ttFb = { ok: false, msg: 'Feedback form not found (the site may have changed).' }; return; }
            setVal(sels[0], D.type); await wait(250);
            setVal(sels[1], D.severity); await wait(250);

            const inputs = [...m.querySelectorAll('input')];
            const ti = inputs.find(i => i.type === 'text');
            const fi = inputs.find(i => i.type === 'file');
            const ta = m.querySelector('textarea');
            if (!ti || !ta) { window.__ttFb = { ok: false, msg: 'Title/description fields not found.' }; return; }
            setVal(ti, D.title); await wait(200);
            setVal(ta, D.description); await wait(200);

            if (D.files.length) {
              if (!fi) { window.__ttFb = { ok: false, msg: 'Screenshot upload field not found.' }; return; }
              const dt = new DataTransfer();
              for (const f of D.files) {
                const bin = atob(f.b64); const u = new Uint8Array(bin.length);
                for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
                dt.items.add(new File([u], f.name, { type: f.type }));
              }
              fi.files = dt.files;
              fi.dispatchEvent(new Event('change', { bubbles: true }));
              let n = 0;
              for (let i = 0; i < 30; i++) {
                await wait(300);
                const mm = m.innerText.match(/SCREENSHOTS[^\n]*?(\d)\s*\/\s*4/i);
                n = mm ? +mm[1] : 0;
                if (n >= D.files.length) break;
              }
              if (n < D.files.length) { window.__ttFb = { ok: false, msg: `The form only accepted ${n} of ${D.files.length} screenshots.` }; return; }
            }

            const btn = [...m.querySelectorAll('button')].find(b => /Submit report/i.test(b.innerText));
            if (!btn) { window.__ttFb = { ok: false, msg: 'Submit button not found.' }; return; }
            btn.click();
            window.__ttFb = { ok: true, msg: 'submitted' };
          })().catch(e => { window.__ttFb = { ok: false, msg: String(e) }; });
          return 'started';
        })()
        """;

    // Session page: (I joined the test) → "I installed it — upload screenshot" → file → "Check screenshot".
    // Result parked on window.__ttIc and polled.
    const string InstallCheckinJs = """
        (() => {
          window.__ttIc = null;
          const D = __DATA__;
          const done = (ok, message, extra) => { window.__ttIc = Object.assign({ ok, message, stage: '', confidence: '', details: '', canConfirm: false }, extra || {}); };
          (async () => {
            const wait = ms => new Promise(r => setTimeout(r, ms));
            const m = document.querySelector('main');
            const txt = () => m.innerText || '';
            const btn = re => [...m.querySelectorAll('button')].find(b => re.test((b.innerText || '').trim()));
            const until = async (fn, ms) => { for (let t = 0; t < ms; t += 300) { const v = fn(); if (v) return v; await wait(300); } return null; };

            if (/Done today/i.test(txt())) return done(false, 'Already checked in today for this app.', { stage: 'done' });

            const joined = btn(/^I joined the test$/i);
            if (joined) {
              if (!D.pressJoined) return done(false, 'The site still shows step 1, "I joined the test".', { stage: 'join' });
              joined.click();
            }

            const inst = await until(() => btn(/I installed it/i), 15000);
            if (!inst) return done(false, 'Couldn\'t find "I installed it — upload screenshot" on the session page (the site may have changed, or the app isn\'t at the install step).');
            inst.click();

            const fi = await until(() => m.querySelector('input[type="file"]'), 10000);
            if (!fi) return done(false, 'Screenshot upload field not found.');
            const bin = atob(D.b64); const u = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
            const dt = new DataTransfer();
            dt.items.add(new File([u], D.name, { type: D.type, lastModified: D.lastModified }));
            fi.files = dt.files;
            fi.dispatchEvent(new Event('input', { bubbles: true }));
            fi.dispatchEvent(new Event('change', { bubbles: true }));

            const check = await until(() => { const b = btn(/^Check screenshot$/i); return b && !b.disabled ? b : null; }, 15000);
            if (!check) return done(false, 'The site didn\'t accept the screenshot ("Check screenshot" stayed disabled).');
            check.click();

            const outcome = await until(() => btn(/^Confirm check-in$/i) || btn(/^Try another screenshot$/i), 75000);
            await wait(500);
            const t = txt();
            const conf = (t.match(/\b(High|Medium|Low|Very low)\s+confidence\b/i) || [''])[0];
            let s = t.indexOf('Check the result below'); if (s < 0) s = t.search(/RESULT/);
            let e = t.indexOf('TAP TO ZOOM', s); if (e < 0) e = t.indexOf('Confirm check-in', s);
            const details = s >= 0 ? t.slice(s, e > s ? e : s + 1500).trim() : '';
            if (!outcome) return done(false, 'No result from the screenshot check (timed out).', { confidence: conf, details });
            const can = !!btn(/^Confirm check-in$/i);
            done(can, can ? 'checked' : 'The site rejected this screenshot.', { confidence: conf, details, canConfirm: can });
          })().catch(e => done(false, String(e)));
          return 'started';
        })()
        """;

    const string ConfirmCheckinJs = """
        (() => {
          const m = document.querySelector('main');
          const b = [...m.querySelectorAll('button')].find(b => /^Confirm check-in$/i.test((b.innerText || '').trim()));
          if (!b) return 'no-button';
          b.click();
          return 'clicked';
        })()
        """;

    const string CheckinStateJs = """
        (() => {
          const m = document.querySelector('main');
          if (!m) return 'wait';
          if (/Done today/i.test(m.innerText)) return 'done';
          const a = m.querySelector('[role="alert"]');
          return a && a.innerText.trim() ? 'error:' + a.innerText.trim().slice(0, 200) : 'wait';
        })()
        """;

    // Daily check-in list: per-session state from the row links ("Check in" = due, "Your check-in" = done).
    const string CheckinListJs = """
        (() => {
          const due = new Set(), done = new Set();
          for (const a of document.querySelectorAll('main a[href*="/dashboard/check-in/"]')) {
            const id = a.getAttribute('href').split('?')[0].split('/').filter(Boolean).pop();
            const t = (a.innerText || '').trim();
            if (/^check in$/i.test(t)) due.add(id);
            else if (/^your check-in$/i.test(t)) done.add(id);
          }
          return { due: [...due], done: [...done] };
        })()
        """;

    const string CheckinRowJs = """
        (() => {
          const id = __ID__;
          const links = [...document.querySelectorAll('main a[href*="/dashboard/check-in/"]')]
            .filter(a => a.getAttribute('href').split('?')[0].split('/').filter(Boolean).pop() === id);
          if (links.some(a => /^your check-in$/i.test((a.innerText || '').trim()))) return 'done';
          if (links.some(a => /^check in$/i.test((a.innerText || '').trim()))) return 'due';
          return 'missing';
        })()
        """;

    const string StartDailyJs = """
        (() => {
          const m = document.querySelector('main');
          if (!m) return 'no-button';
          if (m.querySelector('input[type="file"]')) return 'ready'; // timer already running
          const bs = [...m.querySelectorAll('button')];
          const again = bs.find(b => /^Try another screenshot$/i.test((b.innerText || '').trim()));
          if (again) { again.click(); return 'retry'; } // an earlier unconfirmed upload is still showing
          const b = bs.find(b => /All good/i.test(b.innerText || ''));
          if (!b) return 'no-button';
          b.click();
          return 'clicked';
        })()
        """;

    // Upload on the daily check-in page. Only reads YOUR part of the page (the partner's check-in
    // result further down also says "High confidence"). Result parked on window.__ttDc.
    const string DailyUploadJs = """
        (() => {
          window.__ttDc = null;
          const D = __DATA__;
          const done = (ok, message, extra) => { window.__ttDc = Object.assign({ ok, message, stage: '', confidence: '', details: '', canConfirm: false }, extra || {}); };
          (async () => {
            const wait = ms => new Promise(r => setTimeout(r, ms));
            const m = document.querySelector('main');
            const btn = re => [...m.querySelectorAll('button')].find(b => re.test((b.innerText || '').trim()));
            const until = async (fn, ms) => { for (let t = 0; t < ms; t += 300) { const v = fn(); if (v) return v; await wait(300); } return null; };
            const mine = () => {
              const t = m.innerText || '';
              const i = t.search(/\n\s*FROM [^\n]+\n\s*Their check-in/i);
              return i > 0 ? t.slice(0, i) : t;
            };
            const confRe = /\b(High|Medium|Low|Very low)\s+confidence\b/i;

            const fi = m.querySelector('input[type="file"]');
            if (!fi) return done(false, 'Upload field not found — the check-in timer may not have started.');
            const bin = atob(D.b64); const u = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
            const dt = new DataTransfer();
            dt.items.add(new File([u], D.name, { type: D.type, lastModified: D.lastModified }));
            fi.files = dt.files;
            fi.dispatchEvent(new Event('input', { bubbles: true }));
            fi.dispatchEvent(new Event('change', { bubbles: true }));

            // Some versions of the page need "Check screenshot" pressed; others check straight away.
            const chk = await until(() => { const b = btn(/^Check screenshot$/i); return b && !b.disabled ? b : null; }, 5000);
            if (chk) chk.click();

            const outcome = await until(() =>
              btn(/^Confirm check-in$/i) || btn(/^Try another screenshot$/i) ||
              (confRe.test(mine()) ? 'result' : null) ||
              (/checked in|done today/i.test(mine()) ? 'saved' : null), 75000);
            await wait(800);
            const t = mine();
            const conf = (t.match(confRe) || [''])[0];
            const s = t.search(/RESULT|Check the result/i);
            const details = (s >= 0 ? t.slice(s, s + 1500) : t.slice(-1500)).trim();
            if (!outcome) return done(false, 'No result from the screenshot check (timed out).', { confidence: conf, details });
            if (btn(/^Confirm check-in$/i)) return done(true, 'checked', { confidence: conf, details, canConfirm: true });
            if (btn(/^Try another screenshot$/i)) return done(false, 'The site rejected this screenshot.', { confidence: conf, details });
            done(true, 'saved', { stage: 'saved', confidence: conf, details });
          })().catch(e => done(false, String(e)));
          return 'started';
        })()
        """;

    const string CheckinJs = """
        (() => {
          const m = (document.querySelector('main') || document.body).innerText.match(/(\d+)\s*due today/i);
          return m ? parseInt(m[1], 10) : 0;
        })()
        """;
}

static class WebViewSettingsExtensions
{
    public static void SetDefaultScriptDialogsEnabled(this CoreWebView2Settings s, bool enabled) =>
        s.AreDefaultScriptDialogsEnabled = enabled;
}
