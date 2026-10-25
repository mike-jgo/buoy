using System.Reflection;
using Buoy.Core.Settings;
using Buoy.Core.Storage;
using Buoy.Windows.Dev;
using Buoy.Windows.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;

namespace Buoy.Windows;

public partial class App : Application
{
    public static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private SettingsStore? _settings;
    private NotesDatabase? _database;
    private NoteStore? _notes;
    private MainWindow? _window;
    private NativeShell? _shell;

    public App()
    {
        InitializeComponent();
        // XAML reports a crash in an event handler only as 0xc000027b; keep the real exception.
        UnhandledException += (_, e) => LogCrash(e.Exception);
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            var log = Path.Combine(BuoyPaths.Default.Root, "crash.log");
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} Buoy {Version}\n{exception}\n\n");
        }
        catch (IOException) { }
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

        var paths = BuoyPaths.Default;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Buoy.ico");
        _settings = new SettingsStore(paths);
        _database = NotesDatabase.Open(paths, Version);
        _notes = new NoteStore(_database, context: SynchronizationContext.Current);
        _notes.RestoreSelection(_settings.Value.LastSelectedNoteId);
        _notes.Changed += (_, _) => RememberCurrentNote();

        _window = new MainWindow(_notes, _settings, iconPath);
        _window.QuitRequested += (_, _) => Quit();
        ReportDatabaseState(_database);

        _shell = new NativeShell(iconPath, "Buoy");
        _shell.TrayClicked += _window.TogglePanel;
        _shell.HotkeyPressed += _window.TogglePanel;
        _shell.MenuItems = TrayMenu;
        RegisterHotkey();
        LaunchAtLogin.Apply(_settings.Value.LaunchAtLogin);

        var hotkey = _settings.Value.GlobalShortcut;
        var launchAtLogin = _settings.Value.LaunchAtLogin;
        _settings.Changed += (_, _) =>
        {
            if (_settings.Value.GlobalShortcut != hotkey)
            {
                hotkey = _settings.Value.GlobalShortcut;
                RegisterHotkey();
            }
            if (_settings.Value.LaunchAtLogin != launchAtLogin)
            {
                launchAtLogin = _settings.Value.LaunchAtLogin;
                LaunchAtLogin.Apply(launchAtLogin);
            }
        };

        // A second launch lands here (see Program.Main).
        AppInstance.GetCurrent().Activated += (_, _) => _window.DispatcherQueue.TryEnqueue(_window.ShowPanel);

        if (!commandLine.Contains(LaunchAtLogin.BackgroundArgument))
            _window.ShowPanel();
    }

    private IReadOnlyList<TrayMenuItem?> TrayMenu() =>
    [
        new("New Note", () =>
        {
            _notes!.CreateNote();
            _window!.ShowPanel();
        }),
        null,
        new("About Buoy", ShowAbout),
        null,
        new("Quit Buoy", Quit),
    ];

    private void RegisterHotkey()
    {
        var chord = KeyChord.Parse(_settings!.Value.GlobalShortcut) ?? KeyChord.Default;
        if (!_shell!.RegisterHotkey(chord))
        {
            _window!.ShowNotice(
                $"{chord} is in use",
                "Another app already uses this shortcut, so Buoy can't. Open Buoy from the tray icon for now.",
                InfoBarSeverity.Warning);
        }
    }

    private void ReportDatabaseState(NotesDatabase database)
    {
        switch (database.State)
        {
            case DatabaseState.ReadOnlyNewerVersion:
                _window!.ShowNotice(
                    "Notes are read-only",
                    "They were saved by a newer version of Buoy. Update Buoy to edit them.",
                    InfoBarSeverity.Warning);
                break;
            case DatabaseState.MigrationFailed:
                _window!.ShowNotice(
                    "Couldn't open your notes",
                    "Buoy couldn't update the notes database. Nothing has been changed.",
                    InfoBarSeverity.Error);
                break;
        }
        if (database.BackupFailed)
        {
            _window!.ShowNotice(
                "Couldn't back up your notes",
                "Buoy updated the notes database without a backup.",
                InfoBarSeverity.Warning);
        }
    }

    private void RememberCurrentNote()
    {
        var id = _notes!.CurrentNote?.Id;
        if (id is not null && id != _settings!.Value.LastSelectedNoteId)
            _settings.Update(s => s.LastSelectedNoteId = id);
    }

    private async void ShowAbout()
    {
        _window!.ShowPanel();
        var dialog = new ContentDialog
        {
            XamlRoot = _window.Content.XamlRoot,
            Title = "Buoy",
            Content = $"Version {App.Version} for Windows",
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    public void Quit()
    {
        _notes?.Dispose();
        _settings?.Flush();
        _shell?.Dispose();
        _database?.Dispose();
        _window?.CloseForQuit();
        Exit();
    }
}
