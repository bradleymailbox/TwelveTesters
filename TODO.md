# TwelveTestersTray — remaining work

Goal: one polished main window + tray icon, built and installed the same way as
`C:\bDevStudios\00_Admin\App Deployer Tool` (WPF, .NET 10, Dark/Light theme, per-user Inno Setup installer).

**Data location:** everything the app generates lives in `C:\bDevStudios\00_Backups\TwelveTesters\`
(`AppData\` = `settings.json`, `login.dat`, `log.txt`, `WebView2\`; `Screenshots\` = captures).
On first start the app **copies** (never moves) the old `%LOCALAPPDATA%\TwelveTestersTray` data there.
**Rule for every task: never delete or rename anything in either folder** (the old one stays as the backup).

## Done (committed in `832fb08`)
WPF/.NET 10 shell, solution layout, app icon, single-instance startup, settings/startup/theme services, MVVM helpers,
one main window with all pages (Today, Testing, Report, Check-in, Account, Settings), hidden site browser, no TopMost/MessageBox,
close to tray, simple tray menu, theme, status header, app cards, empty/error states, window position, and phone services
(`AdbService`, `ProcessRunner`, `CaptureManager` in `Services/`).

Order: Step 1 → 2 → 3 → 4 → 5. Fix whatever step 2 finds before moving on.

---

## GUI and branding (done, uncommitted)
- [x] App Deployer look and feel: top header (bDevStudios logo, Lilita One title, pill tabs, Check now), cards, cyan section labels
- [x] Named "Twelve Testers Test Management" (window, product, crash dialog; exe, mutex and Run key keep `TwelveTestersTray`)
- [x] Screenshots filed per app: `Screenshots\<App name>\...` (old session-id folders still read; clean-up now per file)
- [x] Fixed: app dropdown showed the type name, white password box, stretched buttons, upside-down "Check now" while checking, tabs not navigating by keyboard
- [x] Capture after install: Today card shows "Capture & check in" (Day 1) / "Check in" and starts the phone capture straight away; a new install shows a banner with "Capture screenshot" (window open) or a balloon that opens the capture (window hidden). Upload ("Check screenshot") stays a separate click. Verified on the phone with Crumbi.
- [x] Screenshot file access: "Screenshot" button on every Today card and "Capture phone screenshot" on Check-in save to the per-app folder, copy the path to the clipboard and offer Open folder / Copy path (for the site's own upload dialog). Verified on the phone.
- [x] Dismiss to-dos: "Dismiss" on each to-do (with Undo and "Show N dismissed"); a dismissed item returns when its title/priority changes or after it has disappeared from a full check and comes back. Saved in `settings.json` (`DismissedTodos`). Unit-tested; verified live.
- [ ] Check-in page: preview is still large; Open folder click not exercised
- [ ] Not yet restyled to the same polish: Testing and Check-in pages, Today swap cards (reviewed only Today/Report/Account/Settings)

## 1. Back up the data (do first)
- [x] Nothing to copy by hand: the old `%LOCALAPPDATA%\TwelveTestersTray` stays untouched as the backup

- [x] After the first launch, `AppData\` and `Screenshots\` are populated and the app reads from them (verified 2026-11-21: 338/338 screenshots, WebView2 copied, signed in)

## 2. Prove the core flows work (real site + real phone)
- [x] App launches; existing `settings.json`, `login.dat` and captures load unchanged  (verified)
- [ ] Sign in with the saved login, and manually in the Account page  — saved login verified; manual sign-in still to do
- [x] Check now: sessions, credits and to-dos appear; no errors in `log.txt`  (verified: 15 sessions, log clean)
- [ ] Phone: adb found, model shown, installed apps and "opened today" correct; no-phone and adb-missing states show correctly  — connected state verified (SM-N970F, installed/opened-today); no-phone and adb-missing still to do
- [ ] Day 1 check-in and daily check-in (capture → check → confirm)
- [ ] Report submit, with screenshots
- [ ] Second launch brings the running window forward; `--tray` starts hidden; close hides to tray  — single instance verified; window raise unconfirmed (launched from a script, Windows blocked focus); `--tray` hidden start and close-to-tray still to do
- [ ] Dark/Light switch; window position restored

**Found and fixed in step 2:** the hidden site browser was set `IsVisible=false`, so the site never rendered and the first check returned 0 sessions. Now a visible off-screen tool window like the old form (`Services/SiteBrowser.cs`).

## 3. One testing flow on the card (old item 20)
- [x] Start testing → Testing page; Stop (Today card or Testing page) → follow-up panel
- [x] Follow-up panel: Mark tested / Write report / Check in / Done; Report pre-ticks this run's screenshots (max 4)
- [x] Automatic stop: window open → Testing panel + banner (not during a check-in); balloon only if the window isn't in front
- [x] Verified on the real phone: start opens the app, screenshots saved to `Screenshots\`, manual stop ends the run cleanly
- [ ] Still to try by hand: click through Start → Stop → Write report (check ticks), unplug the phone mid-run (auto-stop after ~6 misses), 45-minute limit

## 4. Data safety (old item 22)
- [ ] After a full run, the data folder is unchanged: same files, no renamed fields
- [ ] 30-day capture clean-up and log size limit still work

## 5. Installer and startup (old items 24–28, plus the icon part of 3)
- [ ] `build-installer.ps1`: find ISCC, read `Version` from the `.csproj`, publish, build `artifacts\installer\TwelveTestersTray-Setup-<version>.exe`, `-Install` switch
- [ ] `installer\TwelveTestersTray.iss`: new `AppId`; per-user (`PrivilegesRequired=lowest`, `{localappdata}\Programs\Twelve Testers Tray`);
      `AppMutex=bDevStudios.TwelveTestersTray.SingleInstance`; startup task (`--tray`, checked); Start menu shortcut;
      Run key `TwelveTestersTray` with `uninsdeletevalue`; `SetupIconFile` and `UninstallDisplayIcon` use `app.ico`
- [ ] Warn with a download link if .NET 10 Desktop Runtime or WebView2 Runtime is missing
- [ ] Uninstall removes only installed files and the Run key value; **never** the data folders
- [ ] On start, if the Run key points somewhere other than the installed exe, rewrite it (with `--tray`)
- [ ] Delete the old manual `bin\` / `obj\` builds once the installer works
- [ ] Final test: fresh install; upgrade over a running copy; start with Windows opens in the tray; existing login, settings and captures still there; uninstall leaves the data folder untouched

---

## Removed as non-essential
Refactor into `MainViewModel` / `CheckScheduler` / `NotificationService` and the `TodoAction` enum (old 18),
new-install check-in banner/balloon (21), notification-only-when-hidden polish (23), `README.md` (29), `Refs\FeaturesTODO.txt` (30), desktop-icon install option.
The previous full list is in git history (`6432b56`..`832fb08`).
