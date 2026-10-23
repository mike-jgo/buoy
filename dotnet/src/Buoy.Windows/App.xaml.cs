using Buoy.Windows.Dev;
using Microsoft.UI.Xaml;

namespace Buoy.Windows;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var check = Array.IndexOf(commandLine, "--rtf-check");
        if (check >= 0 && commandLine.Length > check + 2)
        {
            RtfCheck.Start(fixturesDirectory: commandLine[check + 1], reportPath: commandLine[check + 2]);
            return;
        }

        // The panel arrives in Phase 2.
        Exit();
    }
}
