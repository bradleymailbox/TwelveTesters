namespace TwelveTestersTray;

/// <summary>Dialog to enter / change / remove the saved Twelve Testers login.</summary>
public class AccountForm : Form
{
    readonly TextBox email = new() { Width = 300 };
    readonly TextBox password = new() { Width = 300, UseSystemPasswordChar = true };
    readonly CheckBox show = new() { Text = "Show password", AutoSize = true };

    public bool RemoveRequested { get; private set; }
    public string Email => email.Text.Trim();
    /// <summary>Empty means "keep the saved password".</summary>
    public string Password => password.Text;

    public AccountForm(string? savedEmail, bool hasSavedPassword)
    {
        Text = "Twelve Testers — login";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);
        ShowInTaskbar = true;
        TopMost = true;

        email.Text = savedEmail ?? "";
        if (hasSavedPassword) password.PlaceholderText = "(saved — leave blank to keep it)";
        show.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !show.Checked;

        var info = new Label
        {
            AutoSize = true, MaximumSize = new Size(420, 0),
            Text = "The app uses this to sign itself in to twelvetesters.com whenever its session expires.\n" +
                   "It's stored encrypted (Windows DPAPI) so only your Windows account on this PC can read it."
        };

        var save = new Button { Text = "Save && sign in", AutoSize = true, DialogResult = DialogResult.OK };
        var remove = new Button { Text = "Remove saved login", AutoSize = true, Enabled = savedEmail != null };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        remove.Click += (_, _) => { RemoveRequested = true; DialogResult = DialogResult.OK; Close(); };

        save.Click += (s, e) =>
        {
            if (!Email.Contains('@'))
            {
                MessageBox.Show(this, "Enter the email address you use for Twelve Testers.", Text);
                DialogResult = DialogResult.None;
            }
            else if (Password.Length == 0 && !hasSavedPassword)
            {
                MessageBox.Show(this, "Enter your Twelve Testers password.", Text);
                DialogResult = DialogResult.None;
            }
        };

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.Controls.Add(info, 0, 0); grid.SetColumnSpan(info, 2);
        grid.Controls.Add(new Label { Text = "Email", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 12, 8, 0) }, 0, 1);
        grid.Controls.Add(email, 1, 1);
        grid.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) }, 0, 2);
        grid.Controls.Add(password, 1, 2);
        grid.Controls.Add(show, 1, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, save, remove });
        grid.Controls.Add(buttons, 0, 4); grid.SetColumnSpan(buttons, 2);

        Controls.Add(grid);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
