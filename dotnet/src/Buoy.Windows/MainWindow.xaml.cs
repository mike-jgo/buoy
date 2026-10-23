using Buoy.Core.Settings;
using Buoy.Core.Storage;
using Buoy.Windows.Editor;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Buoy.Windows;

/// <summary>
/// The floating panel: borderless (a thin resize border, no title bar), out of
/// the taskbar and Alt+Tab, optionally always on top, on a Mica backdrop. It
/// hides rather than closes; only <see cref="App.Quit"/> closes it.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The panel's first size in DIPs, about the Mac's regular chrome.</summary>
    private static readonly SizeInt32 InitialSize = new(320, 380);
    private static readonly SizeInt32 MinimumSize = new(280, 240);

    private readonly NoteStore _notes;
    private readonly SettingsStore _settings;
    private readonly OverlappedPresenter _presenter;
    private bool _positioned;
    private bool _closing;
    private string? _shownNoteId;

    public MainWindow(NoteStore notes, SettingsStore settings, string iconPath)
    {
        _notes = notes;
        _settings = settings;
        InitializeComponent();

        Title = "Buoy";
        AppWindow.SetIcon(iconPath);
        AppWindow.IsShownInSwitchers = false;

        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        AppWindow.SetPresenter(_presenter);

        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();
        else
            Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        // Alt+F4 hides, like the Mac's close button.
        AppWindow.Closing += (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true;
            HidePanel();
        };
        Header.SizeChanged += (_, _) => UpdateDragRegion();
        Root.Loaded += (_, _) => UpdateDragRegion();

        _notes.Changed += (_, _) => ShowCurrentNote();
        _settings.Changed += (_, _) => ApplySettings();
        ApplySettings();
        ShowCurrentNote();
    }

    public bool IsPanelVisible => AppWindow.IsVisible;

    public void TogglePanel()
    {
        if (AppWindow.IsVisible) HidePanel();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        if (!_positioned)
        {
            SizeAndCenter();
            _positioned = true;
        }
        AppWindow.Show(activateWindow: true);
        Activate();
        // Take the keyboard: the panel is opened to type in. Allowed because the
        // hotkey or tray click that got us here was our input.
        global::Windows.Win32.PInvoke.SetForegroundWindow(Hwnd);
    }

    private global::Windows.Win32.Foundation.HWND Hwnd => new(WinRT.Interop.WindowNative.GetWindowHandle(this));

    public void HidePanel()
    {
        _notes.FlushPendingSaves();
        AppWindow.Hide();
    }

    /// <summary>Shows a message above the note, such as a hotkey that couldn't be registered.</summary>
    public void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    /// <summary>Lets the window close for real; for quitting.</summary>
    public void CloseForQuit()
    {
        _closing = true;
        Close();
    }

    private void ApplySettings()
    {
        var settings = _settings.Value;
        _presenter.IsAlwaysOnTop = settings.AlwaysOnTop;
        Root.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void ShowCurrentNote()
    {
        var note = _notes.CurrentNote;
        TitleText.Text = note?.Title ?? "";
        if (note is null || note.Id == _shownNoteId) return;

        // Read-only until the editor arrives in Phase 3; RichEdit refuses SetText while read-only.
        _shownNoteId = note.Id;
        Editor.IsReadOnly = false;
        RichEditConversion.LoadRtf(Editor.Document, note.ContentRtf);
        Editor.IsReadOnly = true;
    }

    private void SizeAndCenter()
    {
        var scale = Scale();
        _presenter.PreferredMinimumWidth = (int)(MinimumSize.Width * scale);
        _presenter.PreferredMinimumHeight = (int)(MinimumSize.Height * scale);

        var size = new SizeInt32((int)(InitialSize.Width * scale), (int)(InitialSize.Height * scale));
        // The display the pointer is on: the one being used, not wherever the hidden window began.
        global::Windows.Win32.PInvoke.GetCursorPos(out var pointer);
        var area = DisplayArea.GetFromPoint(new PointInt32(pointer.X, pointer.Y), DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - size.Width) / 2,
            area.Y + (area.Height - size.Height) / 2,
            size.Width,
            size.Height));
    }

    private double Scale() => global::Windows.Win32.PInvoke.GetDpiForWindow(Hwnd) / 96.0;

    /// <summary>Makes the header drag the window, the way a title bar would.</summary>
    private void UpdateDragRegion()
    {
        if (Root.XamlRoot is null) return;
        var scale = Root.XamlRoot.RasterizationScale;
        var region = new RectInt32(0, 0, (int)(Header.ActualWidth * scale), (int)(Header.ActualHeight * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Caption, [region]);
    }

    private void OnHideAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HidePanel();
    }
}
