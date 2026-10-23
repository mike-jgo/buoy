using Microsoft.Win32;

namespace Buoy.Windows.Shell;

/// <summary>
/// The <c>HKCU\…\Run</c> value that starts Buoy at sign-in. It passes
/// <see cref="BackgroundArgument"/> so Buoy starts in the tray without opening
/// the panel.
/// </summary>
internal static class LaunchAtLogin
{
    public const string BackgroundArgument = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Buoy";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {BackgroundArgument}");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}
