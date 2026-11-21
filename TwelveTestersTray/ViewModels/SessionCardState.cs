namespace TwelveTestersTray.ViewModels;

public sealed record SessionCardState(string Stage, string BrushKey, string Installed, string Opened, string Checkin)
{
    public static SessionCardState From(SessionInfo session, Snapshot snapshot)
    {
        var known = !string.IsNullOrWhiteSpace(session.Package);
        var phone = known && snapshot.Device?.Connected == true;
        var installed = snapshot.Device?.IsInstalled(session.Package) == true;
        var due = snapshot.SiteCheckinDue.Contains(session.Id);
        var done = snapshot.SiteCheckinDone.Contains(session.Id);
        var setup = Snapshot.IsSetupStage(session);
        var tested = snapshot.ConfirmedToday.Contains(session.Url);
        var stage = done && !due ? "Done today"
            : setup ? (installed ? "Day 1 check-in" : "Setup")
            : tested && !due ? "Done today" : "Daily";
        var brush = stage == "Done today" ? "SuccessBrush"
            : stage == "Day 1 check-in" || due ? "WarningBrush"
            : stage == "Setup" ? "OrangeBrush" : "CyanBrush";
        return new(stage, brush,
            phone ? installed ? "✓" : "✗" : "Unknown",
            phone ? snapshot.Device!.OpenedToday(session.Package) ? "✓" : "✗" : "Unknown",
            due ? "Due" : done ? "Done today" : setup ? "Day 1 pending" : "Not due");
    }
}
