using System.Text.RegularExpressions;

namespace TwelveTestersTray;

/// <summary>Turns what the site (and the test phone) shows into a plain "what do I need to do" list.</summary>
public static class TodoBuilder
{
    public static List<TodoItem> Build(Snapshot s)
    {
        var list = new List<TodoItem>();
        string today = DateTime.Now.ToString("yyyy-MM-dd");

        if (!s.SignedIn)
        {
            list.Add(s.LoginError != null
                ? new TodoItem
                {
                    Key = "login-failed", Priority = Priority.High,
                    Title = "Saved login didn't work",
                    Detail = s.LoginError + " — double-click to update your email/password.",
                    Url = "app:account"
                }
                : new TodoItem
                {
                    Key = "signin", Priority = Priority.High,
                    Title = "Set up your Twelve Testers login",
                    Detail = "Double-click to enter your email and password (stored encrypted on this PC).",
                    Url = "app:account"
                });
            return list;
        }

        var dev = s.Device;
        bool phoneOk = dev is { Connected: true };
        string phone = dev?.Name ?? "your phone";

        if (dev != null && !phoneOk && s.Sessions.Count > 0)
        {
            list.Add(new TodoItem
            {
                Key = "phone:" + dev.Problem, Priority = Priority.Low,
                Title = "Can't check your test phone",
                Detail = dev.Problem ?? "Not connected.",
                Url = ""
            });
        }

        // Per-app check-in items replace the generic one when we know which apps are due.
        if (s.CheckinsDue > 0 && !s.Sessions.Any(x => s.SiteCheckinDue.Contains(x.Id)))
        {
            list.Add(new TodoItem
            {
                Key = $"checkins:{today}:{s.CheckinsDue}", Priority = Priority.High,
                Title = $"{s.CheckinsDue} daily check-in{Plural(s.CheckinsDue)} due today",
                Detail = "Open each app on your phone, then check in.",
                Url = BrowserForm.Base + "/dashboard/check-in"
            });
        }

        foreach (var x in s.Sessions)
        {
            if (!string.IsNullOrEmpty(x.State) && !x.State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
                continue; // ended / cancelled sessions need nothing

            string my = x.MyStatus.Trim();
            string app = string.IsNullOrEmpty(x.App) ? "their app" : x.App;
            bool installed = dev?.IsInstalled(x.Package) == true;
            bool missing = dev?.IsMissing(x.Package) == true;
            bool openedToday = dev?.OpenedToday(x.Package) == true;

            if (my.Equals("Get started", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(installed
                    ? new TodoItem
                    {
                        Key = $"confirm-join:{x.Url}", Priority = Priority.High,
                        Title = $"Upload the install screenshot for {app}",
                        Detail = $"It's installed on {phone}. Double-click: opens it, captures, uploads, and you confirm the Day 1 check-in.",
                        Url = "app:install:" + x.Url
                    }
                    : new TodoItem
                    {
                        Key = $"join:{x.Url}", Priority = Priority.High,
                        Title = $"Join the {app} test",
                        Detail = $"Swap with {x.Partner}. Join the Google Group + opt in, then press \"I joined the test\".",
                        Url = x.Url
                    });
            }
            else if (my.Equals("Join the test", StringComparison.OrdinalIgnoreCase))
            {
                if (missing)
                    list.Add(new TodoItem
                    {
                        Key = $"install:{x.Url}", Priority = Priority.High,
                        Title = $"Install {app} on {phone}",
                        Detail = $"Not installed on the phone yet. Open it in Play Store ({x.Package}) and tap Install, then upload a screenshot.",
                        Url = PlayUrl(x.Package) ?? x.Url
                    });
                else
                    list.Add(new TodoItem
                    {
                        Key = $"screenshot:{x.Url}", Priority = Priority.High,
                        Title = $"Upload a screenshot of {app}",
                        Detail = installed
                            ? $"It's installed on {phone}. Double-click: opens it, captures, uploads, and you confirm the Day 1 check-in."
                            : $"Swap with {x.Partner}. Install it, then double-click to capture and upload the screenshot.",
                        Url = "app:install:" + x.Url
                    });
            }
            else if (my.Contains("feedback", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new TodoItem
                {
                    Key = $"feedback-out:{x.Url}", Priority = Priority.Normal,
                    Title = $"Send feedback on {app}",
                    Detail = $"Swap with {x.Partner} ({x.DaysLeft}).",
                    Url = x.Url
                });
            }
            else if (!string.IsNullOrEmpty(my) &&
                     !Regex.IsMatch(my, "checked in|done|complete", RegexOptions.IgnoreCase))
            {
                if (missing)
                    list.Add(new TodoItem
                    {
                        Key = $"reinstall:{x.Url}", Priority = Priority.High,
                        Title = $"{app} is missing from {phone}",
                        Detail = "You're mid-test — reinstall it from Play Store so daily testing counts.",
                        Url = PlayUrl(x.Package) ?? x.Url
                    });
                else if (s.SiteCheckinDue.Contains(x.Id))
                    list.Add(new TodoItem
                    {
                        Key = $"checkin:{x.Url}:{today}", Priority = Priority.High,
                        Title = $"Check in on the site for {app}",
                        Detail = (openedToday ? $"Opened on {phone} today. " : "") +
                                 $"Use it for a few minutes, then double-click: starts the check-in, captures and uploads; you confirm. ({x.DaysLeft})",
                        Url = "app:checkin:" + x.Url
                    });
                else if (!s.ConfirmedToday.Contains(x.Url))
                    list.Add(new TodoItem
                    {
                        Key = $"daily:{x.Url}:{today}", Priority = Priority.Normal,
                        Title = $"Open and test {app} today",
                        Detail = (openedToday
                                     ? $"Opened on {phone} today — confirm once you've used it. "
                                     : phoneOk && x.Package != null ? $"Not opened on {phone} yet today. " : "")
                                 + $"Swap with {x.Partner} ({x.DaysLeft}). Double-click to test & confirm.",
                        Url = "app:test"
                    });
                // confirmed today → nothing to do for this one
            }

            if (x.PartnerStatus.Equals("Get started", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new TodoItem
                {
                    Key = $"partner:{x.Url}", Priority = Priority.Low,
                    Title = $"{x.Partner} hasn't joined your test yet",
                    Detail = "Optional: send them a reminder from the session page.",
                    Url = x.Url
                });
            }
        }

        foreach (var r in s.Requests)
        {
            list.Add(new TodoItem
            {
                Key = $"request:{r.From}:{r.TheirApp}", Priority = Priority.Normal,
                Title = $"Reply to {(string.IsNullOrEmpty(r.From) ? "a" : r.From + "'s")} swap request",
                Detail = $"They want to test your app and offered {(string.IsNullOrEmpty(r.TheirApp) ? "theirs" : r.TheirApp)}. Accept or decline.",
                Url = BrowserForm.Base + "/dashboard/requests"
            });
        }

        if (s.FeedbackIn > 0)
        {
            list.Add(new TodoItem
            {
                Key = $"feedback-in:{s.FeedbackIn}", Priority = Priority.Normal,
                Title = $"Read {s.FeedbackIn} feedback report{Plural(s.FeedbackIn)} on your app",
                Detail = "Testers' bug reports and notes.",
                Url = BrowserForm.Base + "/dashboard/feedback"
            });
        }

        return list.OrderBy(t => t.Priority).ToList();
    }

    static string? PlayUrl(string? pkg) =>
        string.IsNullOrEmpty(pkg) ? null : $"https://play.google.com/store/apps/details?id={pkg}";

    static string Plural(int n) => n == 1 ? "" : "s";
}
