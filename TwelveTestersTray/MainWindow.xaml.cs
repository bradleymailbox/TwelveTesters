using System.Windows;
using System.ComponentModel;

namespace TwelveTestersTray;

public partial class MainWindow : Window
{
    public event Action? ShowTaskListRequested;
    public bool AllowClose { get; set; }

    public MainWindow() => InitializeComponent();

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OpenTasks_Click(object sender, RoutedEventArgs e) => ShowTaskListRequested?.Invoke();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
