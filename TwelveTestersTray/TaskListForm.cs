namespace TwelveTestersTray;

/// <summary>The "what do I need to do" window shown from the tray.</summary>
public class TaskListForm : Form
{
    readonly Label header = new() { Dock = DockStyle.Top, Height = 52, Padding = new Padding(10, 8, 10, 0) };
    readonly ListView list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false
    };
    readonly FlowLayoutPanel buttons = new()
    {
        Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6)
    };

    public event Action? CheckNowClicked;
    public event Action<TodoItem>? ItemOpened;

    public TaskListForm()
    {
        Text = "Twelve Testers — to do";
        Font = new Font("Segoe UI", 9.75f);
        Size = new Size(900, 440);
        MinimumSize = new Size(500, 260);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;

        list.Columns.Add("", 70);
        list.Columns.Add("To do", 280);
        list.Columns.Add("Details", 380);
        list.Groups.Add(new ListViewGroup("High", "Do now"));
        list.Groups.Add(new ListViewGroup("Normal", "Today"));
        list.Groups.Add(new ListViewGroup("Low", "Optional"));
        list.ItemActivate += (_, _) => OpenSelected();

        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Hide();
        var site = new Button { Text = "Open Twelve Testers", AutoSize = true };
        site.Click += (_, _) => TrayContext.OpenUrl(BrowserForm.Base + "/dashboard");
        var open = new Button { Text = "Open selected", AutoSize = true };
        open.Click += (_, _) => OpenSelected();
        var check = new Button { Text = "Check now", AutoSize = true };
        check.Click += (_, _) => CheckNowClicked?.Invoke();
        buttons.Controls.AddRange(new Control[] { close, site, open, check });

        Controls.Add(list);
        Controls.Add(header);
        Controls.Add(buttons);
    }

    void OpenSelected()
    {
        if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is TodoItem t) ItemOpened?.Invoke(t);
    }

    public void SetData(Snapshot? snap, List<TodoItem> todos, string? error, bool checking)
    {
        string status = checking ? "Checking…"
            : snap == null ? "Not checked yet"
            : $"Last checked {snap.At:t}";
        if (snap?.Credits is int c) status += $"   ·   {c} credits";
        if (snap != null && snap.SignedIn) status += $"   ·   {snap.Sessions.Count} active session{(snap.Sessions.Count == 1 ? "" : "s")}";
        if (snap?.Device is { } d)
        {
            if (d.Connected)
            {
                var pkgs = snap.Sessions.Select(x => x.Package).OfType<string>().Distinct().ToList();
                int inst = pkgs.Count(p => d.Installed.Contains(p));
                int opened = pkgs.Count(d.OpenedToday);
                status += $"   ·   📱 {d.Name}: {inst}/{pkgs.Count} test apps installed, {opened} opened today";
            }
            else status += "   ·   📱 phone not checked";
        }
        if (error != null) status += $"\nLast check failed: {error}";
        else if (snap != null) status += todos.Count == 0 ? "\nNothing to do right now 🎉" : $"\n{todos.Count} item{(todos.Count == 1 ? "" : "s")} — double-click one to open it";
        header.Text = status;

        list.BeginUpdate();
        list.Items.Clear();
        foreach (var t in todos)
        {
            var label = t.Priority switch { Priority.High => "● Now", Priority.Normal => "Today", _ => "Optional" };
            var item = new ListViewItem(new[] { label, t.Title, t.Detail })
            {
                Tag = t,
                Group = list.Groups[t.Priority.ToString()],
                ToolTipText = t.Detail
            };
            if (t.Priority == Priority.High) item.ForeColor = Color.FromArgb(190, 60, 20);
            list.Items.Add(item);
        }
        list.ShowItemToolTips = true;
        list.EndUpdate();
    }

    public void ShowNearTray()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        if (!Visible) Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
