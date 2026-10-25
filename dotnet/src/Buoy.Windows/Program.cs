using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Buoy.Windows;

public static class Program
{
    /// <summary>
    /// One Buoy at a time: a second launch hands its activation to the first,
    /// which shows its panel, and exits. The <c>--rtf-check</c> dev tool runs
    /// alongside a normal instance.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (!args.Contains("--rtf-check"))
        {
            // A test profile (BUOY_DATA_DIR) is its own instance, alongside the real one.
            var paths = Buoy.Core.Storage.BuoyPaths.Current;
            var main = AppInstance.FindOrRegisterForKey(paths.IsTestProfile ? $"Buoy:{paths.Root.ToLowerInvariant()}" : "Buoy");
            if (!main.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                // Off the STA thread: waiting on it here would deadlock the redirect.
                Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
                return;
            }
        }

        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
