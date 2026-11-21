# TwelveTestersTray — upgrade TODO

Goal: one polished main window + tray icon, built and installed the same way as
`C:\bDevStudios\00_Admin\App Deployer Tool` (WPF, .NET 10, Dark/Light theme, per-user Inno Setup installer).

**Rule for every task: never move, rename or delete anything in `%LOCALAPPDATA%\TwelveTestersTray\`**
(`settings.json`, `login.dat`, `captures\`, `WebView2\`, `log.txt`).

Suggested order: Phase 1 → 13 → Phase 2 → Phase 4 → 14–17 → Phase 5 → Phase 6.

---

## Phase 1 — Project and app shell

- [x] **1. Move to WPF on .NET 10**
  - [x] `net10.0-windows`, `UseWPF=true`, `UseWindowsForms=true` (legacy Forms stay until item 7; then NotifyIcon only)
  - [x] `<Using Remove="System.Windows.Forms" />` and `<Using Remove="System.Drawing" />` (as in `AppDeployer.csproj`)
  - [x] Add `Product`, `AssemblyTitle`, `Company=bDevStudios`, `Version=1.0.0`, `ApplicationIcon=app.ico`
  - [x] Keep `AssemblyName=TwelveTestersTray` (exe name and Run key value stay the same)
  - [x] WebView2: use the WPF control, update the package (e.g. 1.0.3719.77)
  - [x] Keep `System.Security.Cryptography.ProtectedData`
  - Done when: an empty WPF shell builds and runs on .NET 10

- [x] **2. Solution and folder layout**
  - [x] `TwelveTestersTray.slnx` at the root, project in `TwelveTestersTray\`
  - [x] Folders: `Models\`, `Services\`, `ViewModels\`, `Views\`, `Themes\`, `Fonts\`, `installer\`, `artifacts\`, `Refs\`
  - [x] Copy App Deployer's `.gitignore`
  - Done when: `dotnet build TwelveTestersTray.slnx` passes

- [ ] **3. App icon**
  - [x] Create `app.ico` (16/32/48/256) based on the orange "12" circle
  - [ ] Use it for the exe, window, tray, `SetupIconFile` and `UninstallDisplayIcon`
        (exe, window and tray done; installer fields follow in item 25)
  - [x] Keep the live tray badge (count / ✓ / grey) drawn over the base icon (reuse `SetIcon`)

- [x] **4. App startup (`App.xaml.cs`)** — copy App Deployer's
  - [x] `MutexName = "bDevStudios.TwelveTestersTray.SingleInstance"` (installer's `AppMutex` follows in item 25)
  - [x] Named "Show" event: a second launch brings the running window forward (no MessageBox)
  - [x] `--tray` argument starts hidden
  - [x] `DispatcherUnhandledException` + `TaskScheduler.UnobservedTaskException` → `Log.Write`
  - Done when: launching the exe while it's in the tray brings the window to the front

- [ ] **5. Settings, startup and theme services**
  - [ ] `Services/StartupRegistration.cs` with `ValueName="TwelveTestersTray"`, `TrayArgument="--tray"`
        (fixes: today's Run entry has no `--tray` and points at whatever copy last ran)
  - [ ] Keep `Settings` at `%LOCALAPPDATA%\TwelveTestersTray\settings.json` — don't rename fields
  - [ ] New optional fields: `Theme`, `CloseToTray`, `TrayHintShown`, `WindowBounds`, `LastPage`
  - [ ] Copy `Services/ThemeManager.cs` as is
  - Done when: the existing `settings.json` loads unchanged

- [ ] **6. MVVM helpers**
  - [ ] Copy `Mvvm.cs` and the needed converters from App Deployer
  - [ ] Add `AsyncRelayCommand` that can't run twice at once (replaces `checking`, `phoneRefreshing`, `accountOpen` flags)

---

## Phase 2 — One main window (fixes windows hiding behind others)

- [ ] **7. `MainWindow` with sidebar pages** (one UserControl + view model per page)
  - [ ] **Today** (replaces `TaskListForm` + `ReminderForm`): to-do items, then one card per swap
        (app, partner, days left, stage, installed ✓/✗, opened today ✓/✗, check-in due/done) with
        Install on phone / Start testing / Stop / Check in / Mark tested / Report / ⋯ menu
  - [ ] **Testing** (replaces capture parts of `ReminderForm` and the `AfterTesting` MessageBox):
        elapsed vs 45-min limit, screenshot count, live thumbnail, Stop, then Mark tested / Write report / Check in / Done
  - [ ] **Report** (replaces `FeedbackForm`): type, severity, title, description, device line,
        screenshot grid with max count, Add file, Submit with inline result, "Open site form instead"
  - [ ] **Check-in** (replaces `InstallCheckinForm`, Day 1 and daily): capture/pick → preview with age →
        Check screenshot → confidence/details → Confirm; yes/no prompts become inline warnings
  - [ ] **Account** (replaces `AccountForm` + "Sign in manually"): email/password, Save, Remove, last status, manual sign-in
  - [ ] **Settings** (replaces tray submenus): check interval, reminder times, screenshot interval, phone checks,
        adb path (Browse + "Found at…"), theme, Start with Windows, close to tray, Open data folder, Open log
  - Done when: none of the old Form classes remain

- [ ] **8. Sign-in browser and hidden site browser**
  - [ ] `Services/SiteBrowser.cs`: one `CoreWebView2Environment` (user data folder unchanged:
        `%LOCALAPPDATA%\TwelveTestersTray\WebView2`), controller on a never-shown `HwndSource`, bounds 1500×950
  - [ ] Move unchanged from `BrowserForm`: `ScrapeAsync`, `SubmitFeedbackAsync`, `BeginInstallCheckinAsync`,
        `BeginDailyCheckinAsync`, `ConfirmInstallCheckinAsync`, `CancelInstallCheckin`, `busy` semaphore, dialog handlers
  - [ ] Visible sign-in: WPF `WebView2` on the Account page using the same environment (shared cookies);
        "Done signing in" runs a check
  - [ ] Don't place banners/toasts over the WebView2 area (WPF can't draw on top of it)
  - Done when: no off-screen window exists; manual sign-in happens in the main window

- [ ] **9. Remove TopMost and MessageBoxes**
  - [ ] Delete every `TopMost = true` (`AccountForm`, `ReminderForm`)
  - [ ] Replace ~15 `MessageBox.Show` calls with inline banners (info/success/warning/error) or inline confirm bars
  - [ ] Any remaining dialog (file picker) gets `Owner = MainWindow`
  - Done when: `MessageBox.Show` only appears in the global crash handler

- [ ] **10. One way to bring the window forward**
  - [ ] `MainWindow.ShowFromTray(page, session)`: Show, restore, Activate, Topmost on/off, Focus, navigate
  - [ ] Used by tray click, balloon click, second launch, reminders and new-install detection
  - [ ] Replace `balloonAction` with a stored (page, session) target

- [ ] **11. Close to tray**
  - [ ] Copy App Deployer's `OnClosing`: hide + one-time "Still running in the tray" balloon, or exit if turned off
  - [ ] `ExitApplication()` warns inline if a capture or report is running, then does today's `Exit()` clean-up

- [ ] **12. Simpler tray menu** (copy `TrayIcon.cs` structure)
  - [ ] Open (bold), Today, Testing (while capturing), Check now, Test next app, Open Twelve Testers website, Exit
  - [ ] Remove the feedback / interval / reminder / screenshot / login submenus
  - [ ] Keep the tooltip status text (trimmed to the NotifyIcon limit)

---

## Phase 3 — Polished look

- [ ] **13. Theme**
  - [ ] Copy `Themes/Theme.xaml`, `Dark.xaml`, `Light.xaml` and `Fonts/` (with licence files); fix the `local:` namespace
  - [ ] Use `WindowBackground`, `Card`, `Chip`, `PrimaryGradient`, Success/Warning/Error brushes
  - [ ] No hex colours in views — `DynamicResource` only

- [ ] **14. Status header** on every page
  - [ ] Signed in / "Login needed" chip, credits, due today, check-ins due, feedback in, requests in
  - [ ] Phone: model connected / no phone / adb not found
  - [ ] "Checked n min ago" + refresh button (spins while checking)

- [ ] **15. App cards**
  - [ ] State colours, stage chips (Setup → Day 1 check-in → Daily → Done today)
  - [ ] Today's latest capture thumbnail
  - [ ] "Package unknown" hint; Install/Play actions disabled until known

- [ ] **16. Loading / empty / error states in the window**
  - [ ] First check: placeholder list
  - [ ] Signed out / login failed: card with Fix login (keep the no-retry rule to avoid lockouts)
  - [ ] WebView2 runtime missing: card with download link
  - [ ] adb not found / no device: card with adb path + Retry
  - [ ] Nothing to do: "All done for today ✓"

- [ ] **17. Window position**
  - [ ] Save/restore size, position, state in `Settings.WindowBounds`; clamp to a visible monitor
  - [ ] Set a minimum window size

---

## Phase 4 — Making the current features work smoothly

- [ ] **18. Separate logic from UI**
  - [ ] Move `TrayContext.cs` logic into `ViewModels/MainViewModel.cs`
  - [ ] `Services/CheckScheduler.cs`: interval timer, 60 s phone refresh, reminder timer
        (`CheckNowAsync`, `RefreshPhoneAsync`, `CheckReminder` logic unchanged)
  - [ ] `Services/NotificationService.cs`
  - [ ] Keep `TodoBuilder`, `CredentialStore`, `Log` (just move into `Services/` / `Models/`)
  - [ ] Replace `app:` URLs in `TodoItem.Url` with `enum TodoAction { OpenUrl, Test, Account, SignIn, InstallCheckin, DailyCheckin }` + session

- [ ] **19. Phone services**
  - [ ] `DeviceChecker` → `Services/AdbService.cs` (instance class holding the adb path)
  - [ ] `CaptureManager` → `Services/`
  - [ ] Use App Deployer's `ProcessRunner` pattern (timeouts, output capture)
  - [ ] Same adb search order as App Deployer
  - [ ] All adb calls stay off the UI thread

- [ ] **20. One testing flow on the card**
  - [ ] Start testing → Testing page → Stop or automatic stop → Mark tested / Write report (this run's screenshots pre-ticked) / Check in
  - [ ] `EndedAutomatically` shows the same follow-up panel; balloon only when the window isn't focused

- [ ] **21. New install opens the check-in**
  - [ ] Window open: banner "X is installed — do Day 1 check-in" with a button
  - [ ] Window hidden: balloon that opens Check-in for that app

- [ ] **22. Keep all data** (must-have)
  - [ ] Same folder, `settings.json`, `login.dat` (DPAPI, entropy `TwelveTestersTray.login.v1`), `captures\`, `WebView2\`, `log.txt`
  - [ ] New settings fields optional; no field renames
  - [ ] Keep the 30-day capture clean-up and the log size limit

- [ ] **23. Notifications**
  - [ ] Only notify when the window isn't in front (copy App Deployer's `OnNotification`)
  - [ ] Every notification click opens the matching page/app via `ShowFromTray`
  - [ ] Keep "new to-do" and reminder logic (`Notify`, `CheckReminder`, snooze) unchanged

---

## Phase 5 — Installer (same as App Deployer)

- [ ] **24. `build-installer.ps1`** (copy App Deployer's, drop the site-content import)
  - [ ] Find ISCC (Inno Setup 6); read `Version` from the `.csproj`
  - [ ] `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o artifacts\publish`
  - [ ] ISCC → `artifacts\installer\TwelveTestersTray-Setup-<version>.exe`
  - [ ] `-Install` switch: stop the running app, silent install with `/TASKS=startup`, start it

- [ ] **25. `installer\TwelveTestersTray.iss`** (copy `AppDeployer.iss`)
  - [ ] New `AppId` GUID; `AppName=Twelve Testers Tray`; `AppPublisher=bDevStudios`
  - [ ] `PrivilegesRequired=lowest`; `DefaultDirName={localappdata}\Programs\Twelve Testers Tray`
  - [ ] `AppMutex=bDevStudios.TwelveTestersTray.SingleInstance`, `CloseApplications=yes`
  - [ ] Tasks: startup (`--tray`, checked), desktop icon (unchecked)
  - [ ] Start menu shortcut; Run key value `TwelveTestersTray` with `uninsdeletevalue`
  - [ ] "Open now" post-install option; shows in Settings > Apps

- [ ] **26. Prerequisite checks in the installer**
  - [ ] .NET 10 Desktop Runtime (`{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*`)
  - [ ] WebView2 Runtime (EdgeUpdate client key `{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` in HKLM/HKCU) — warn with download link

- [ ] **27. Safe uninstall**
  - [ ] Remove only the Run key value and installed files
  - [ ] **Never delete `%LOCALAPPDATA%\TwelveTestersTray`** (no `[UninstallDelete]` for it)

- [ ] **28. Clean up old copies**
  - [ ] Delete `bin\` / `obj\` manual builds once the installer works
  - [ ] On start, if the Run key points somewhere other than the installed exe, rewrite it (with `--tray`)

---

## Phase 6 — Docs and finishing

- [ ] **29. `README.md`** with App Deployer's sections: Install, Run from source, Theme, System tray, What it does, Data location
- [ ] **30. Git**: `git init`, first commit; add `Refs\FeaturesTODO.txt` for future ideas
- [ ] **31. Smoke test**
  - [ ] Fresh install; upgrade over a running copy
  - [ ] Start with Windows → starts hidden in the tray
  - [ ] Second launch brings the window forward
  - [ ] No window ever opens behind another
  - [ ] Existing login, settings and captures still there after install
  - [ ] Sign in (saved login and manual), Day 1 check-in, daily check-in, report submit
  - [ ] Testing with phone plugged in and unplugged; automatic stop
  - [ ] Dark/Light switch; window position restored
  - [ ] Uninstall leaves `%LOCALAPPDATA%\TwelveTestersTray` untouched
