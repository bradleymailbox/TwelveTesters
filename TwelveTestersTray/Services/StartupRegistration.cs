using Microsoft.Win32;

namespace TwelveTestersTray.Services;

/// <summary>Per-user Windows startup entry for the installed tray application.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "TwelveTestersTray";
    public const string TrayArgument = "--tray";

    private static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }

    /// <summary>Keep an existing startup choice pointed at this executable and hidden in the tray.</summary>
    public static void RepairExisting()
    {
        if (string.IsNullOrWhiteSpace(Environment.ProcessPath)) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string existing &&
            !string.Equals(existing, Command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, Command);
    }
}
