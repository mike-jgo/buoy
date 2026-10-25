using System.Numerics;
using Buoy.Core.Documents;
using Buoy.Core.Settings;
using Buoy.Core.Storage;
using Buoy.Windows.Editor;
using Buoy.Windows.Shell;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Buoy.Windows;

/// <summary>
/// The floating panel, laid out like the Mac's: borderless (a thin resize
/// border, no title bar), out of the taskbar and Alt+Tab, optionally always on
/// top, on thin acrylic glass. It hides rather than closes; only
/// <see cref="App.Quit"/> closes it.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The panel's first size in DIPs: the Mac's glass at regular density, plus room for Windows text.</summary>
    private static readonly SizeInt32 InitialSize = new(316, 400);
    private static readonly SizeInt32 MinimumSize = new(292, 300);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(2);

    private readonly NoteStore _notes;
    private readonly SettingsStore _settings;
    private readonly OverlappedPresenter _presenter;
    private readonly BuoyEditor _editor;
    private readonly AllNotesPanel _allNotes;
    private readonly TrafficLights _lights;
    private bool _expanded;
    private readonly DispatcherTimer _toastTimer = new() { Interval = ToastDuration };
    private bool _positioned;
    private bool _closing;
    private bool _showingTitle;

    /// <summary>Raised to quit the app (from Settings).</summary>
    public event EventHandler? QuitRequested;

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

        if (GlassBackdrop.IsSupported)
            SystemBackdrop = new GlassBackdrop();
        else
            Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        // Alt+F4 hides, like the close button.
        AppWindow.Closing += (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true;
            HidePanel();
        };
        Header.SizeChanged += (_, _) => UpdateDragRegion();
        HeaderButtons.SizeChanged += (_, _) => UpdateDragRegion();
        Root.Loaded += (_, _) => UpdateDragRegion();
        Root.PreviewKeyDown += OnShortcut;
        _toastTimer.Tick += (_, _) => HideToast();
        Motion.SetOpacity(Toast, 0);
        EditorHost.SizeChanged += (_, e) =>
            EditorHost.Clip = new RectangleGeometry { Rect = new global::Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

        // Red hides, green toggles the tall size; yellow (Harbor Mode) stays greyed out until it exists.
        _lights = new TrafficLights(close: HidePanel, minimize: null, expand: ToggleExpanded)
        {
            Margin = new Thickness(9, 14, 0, 0), // 15pt from the glass edge, centred in the 28pt row
            VerticalAlignment = VerticalAlignment.Top,
        };
        Header.Children.Add(_lights);
        _lights.SizeChanged += (_, _) => UpdateDragRegion();
        Activated += (_, e) => _lights.IsActive = e.WindowActivationState != WindowActivationState.Deactivated;

        // The Mac title has no clear button. WinUI has no switch for a text box's
        // "×", and its visual states only toggle visibility, so shrink it to nothing.
        TitleBox.Loaded += (_, _) =>
        {
            if (FindByName(TitleBox, "DeleteButton") is { } clear)
            {
                clear.Width = 0;
                clear.Opacity = 0;
                clear.IsHitTestVisible = false;
            }
        };

        _editor = new BuoyEditor(EditorBox, notes, settings.Value.FontSize);
        _editor.ContentChanged += (_, _) => UpdateInfo();
        _editor.TodoToggled += PopTodo;
        Toolbar.IsHitTestVisible = !notes.IsReadOnly;
        Toolbar.Opacity = notes.IsReadOnly ? 0.5 : 1;
        TitleBox.IsReadOnly = notes.IsReadOnly;
        // All Notes opens over the title, toolbar and note, below the control row.
        _allNotes = new AllNotesPanel(notes) { Margin = new Thickness(2, 0, 2, 8) };
        Grid.SetRow(_allNotes, 1);
        Grid.SetRowSpan(_allNotes, 4);
        Root.Children.Add(_allNotes);
        _allNotes.NoteChosen += id =>
        {
            _notes.SwitchNote(id);
            _editor.Focus();
        };
        _allNotes.DeleteRequested += note => DeleteNote(note);
        _allNotes.Closed += () => { if (AppWindow.IsVisible) _editor.Focus(); };

        SettingsFlyout.Content = new SettingsPanel(settings, () => QuitRequested?.Invoke(this, EventArgs.Empty));

        _notes.Changed += (_, _) => ShowCurrentNote();
        _settings.Changed += (_, _) => ApplySettings();
        ApplySettings();
        ShowCurrentNote();
    }

    // MARK: - Showing and hiding

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
        TakeForeground();
        _editor.Focus();
    }

    public void HidePanel()
    {
        _allNotes.Close();
        _notes.FlushPendingSaves();
        AppWindow.Hide();
    }

    /// <summary>Lets the window close for real; for quitting.</summary>
    public void CloseForQuit()
    {
        _closing = true;
        Close();
    }

    private global::Windows.Win32.Foundation.HWND Hwnd => new(WinRT.Interop.WindowNative.GetWindowHandle(this));

    /// <summary>
    /// Takes the keyboard: the panel is opened to type in. Windows usually allows
    /// it after our own hotkey or tray click, but refuses it when another app
    /// holds the foreground lock; then joining that app's input thread for a
    /// moment is the documented way through.
    /// </summary>
    private unsafe void TakeForeground()
    {
        var hwnd = Hwnd;
        if (global::Windows.Win32.PInvoke.SetForegroundWindow(hwnd)) return;

        var foreground = global::Windows.Win32.PInvoke.GetForegroundWindow();
        var foregroundThread = global::Windows.Win32.PInvoke.GetWindowThreadProcessId(foreground, null);
        var ourThread = global::Windows.Win32.PInvoke.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == ourThread) return;

        global::Windows.Win32.PInvoke.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            global::Windows.Win32.PInvoke.BringWindowToTop(hwnd);
            global::Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
        }
        finally
        {
            global::Windows.Win32.PInvoke.AttachThreadInput(ourThread, foregroundThread, false);
        }
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

    /// <summary>
    /// The control row drags the window, the way a title bar would, between the
    /// traffic lights and the buttons, from the window's top edge down.
    /// </summary>
    private void UpdateDragRegion()
    {
        if (Root.XamlRoot is null) return;
        var scale = Root.XamlRoot.RasterizationScale;
        var left = _lights.TransformToVisual(Root).TransformPoint(default).X + _lights.ActualWidth;
        var right = HeaderButtons.TransformToVisual(Root).TransformPoint(default).X;
        var region = new RectInt32(
            (int)(left * scale), 0,
            (int)(Math.Max(0, right - left) * scale), (int)((Header.ActualHeight + Root.Padding.Top) * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Caption, [region]);
    }

    /// <summary>
    /// The green light: between the panel's size and a tall one (the Mac's
    /// expandedHeight, 780), keeping the top edge where it is.
    /// </summary>
    private void ToggleExpanded()
    {
        var scale = Scale();
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var position = AppWindow.Position;
        var target = _expanded ? InitialSize.Height * scale : Math.Min(780 * scale, area.Y + area.Height - position.Y);
        _expanded = !_expanded;
        AnimateHeight((int)target);
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _heightTimer;

    /// <summary>
    /// Grows or shrinks the window to <paramref name="target"/> pixels over 0.25s,
    /// easing in and out, with its top edge still (the Mac's animateHeight).
    /// Instant under reduced motion: a window either travels or it doesn't.
    /// </summary>
    private void AnimateHeight(int target)
    {
        _heightTimer?.Stop();
        var start = AppWindow.Size.Height;
        var width = AppWindow.Size.Width;
        if (Motion.IsReduced || start == target)
        {
            AppWindow.Resize(new SizeInt32(width, target));
            return;
        }

        var duration = TimeSpan.FromSeconds(0.25);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed / duration);
            var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease in-out
            AppWindow.Resize(new SizeInt32(width, (int)Math.Round(start + (target - start) * eased)));
            if (t >= 1) timer.Stop();
        };
        _heightTimer = timer;
        timer.Start();
    }

    private bool _sliding;

    /// <summary>
    /// Previous/next note: the outgoing note slides off one side as the new one
    /// slides in from the other (the Mac's slide, spring 0.3 / 0.8). The outgoing
    /// note is a snapshot, since there is one editor. No slide under reduced motion.
    /// </summary>
    private async void SlideToNote(bool forward)
    {
        var from = _notes.CurrentNote?.Id;
        if (Motion.IsReduced || _sliding || EditorHost.ActualWidth <= 0)
        {
            if (forward) _notes.NextNote(); else _notes.PreviousNote();
            return;
        }

        _sliding = true;
        try
        {
            var snapshot = new RenderTargetBitmap();
            await snapshot.RenderAsync(EditorBox);
            SlideSnapshot.Source = snapshot;
            SlideSnapshot.Width = EditorBox.ActualWidth;
            SlideSnapshot.Height = EditorBox.ActualHeight;
            SlideSnapshot.Stretch = Stretch.Fill;

            if (forward) _notes.NextNote(); else _notes.PreviousNote();
            if (_notes.CurrentNote?.Id == from) return; // only one note

            var width = (float)EditorHost.ActualWidth;
            var direction = forward ? 1 : -1;
            SlideSnapshot.Visibility = Visibility.Visible;
            Motion.SlideIn(SlideSnapshot, Vector3.Zero, 0.3, 0.8);
            Motion.SlideOut(SlideSnapshot, new Vector3(-direction * width, 0, 0), 0.3, 0.8, () =>
            {
                SlideSnapshot.Visibility = Visibility.Collapsed;
                SlideSnapshot.Source = null;
            });
            Motion.SlideIn(EditorBox, new Vector3(direction * width, 0, 0), 0.3, 0.8);
        }
        finally
        {
            _sliding = false;
        }
        _editor.Focus();
    }

    /// <summary>
    /// A to-do box changing: an accent check pops in (scale 0.55 to 1, overshooting)
    /// over the new box and fades, or the filled box shrinks away when unchecked,
    /// after the Mac's 0.32s checkbox animation. The glyph in the note changes at
    /// once underneath. Nothing under reduced motion, as on the Mac.
    /// </summary>
    private void PopTodo(global::Windows.Foundation.Rect box, bool isChecked)
    {
        if (Motion.IsReduced) return;
        var origin = EditorBox.TransformToVisual(Effects).TransformPoint(new global::Windows.Foundation.Point(box.X, box.Y));
        var size = Math.Max(box.Width, box.Height);
        var icon = new FontIcon
        {
            Glyph = "",
            FontSize = size,
            Foreground = ThemeBrushes.Get<Brush>(Root, "AccentFillColorDefaultBrush"),
            Width = size,
            Height = size,
        };
        Canvas.SetLeft(icon, origin.X);
        Canvas.SetTop(icon, origin.Y + (box.Height - size) / 2);
        Effects.Children.Add(icon);
        icon.UpdateLayout();

        var visual = Motion.VisualOf(icon);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        if (isChecked)
        {
            visual.Scale = new Vector3(0.55f, 0.55f, 1);
            scale.InsertKeyFrame(0.6f, Vector3.One, Motion.EaseOutBack(compositor));
            scale.InsertKeyFrame(1, Vector3.One);
        }
        else
        {
            scale.InsertKeyFrame(1, new Vector3(0.55f, 0.55f, 1), Motion.EaseOut(compositor));
        }
        scale.Duration = TimeSpan.FromSeconds(0.32);
        visual.StartAnimation("Scale", scale);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(isChecked ? 0.6f : 0f, 1);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = TimeSpan.FromSeconds(0.32);
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => Effects.Children.Remove(icon));
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    // MARK: - Settings

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
        if (EditorBox.FontSize != settings.FontSize) _editor.SetFontSize(settings.FontSize);
        UpdateInfo();
    }

    // MARK: - The current note

    private void ShowCurrentNote()
    {
        var note = _notes.CurrentNote;
        if (note is null) return;

        // Don't rewrite the title under the caret while it's being typed.
        if (TitleBox.Text != note.Title && (note.Id != _editor.NoteId || TitleBox.FocusState == FocusState.Unfocused))
        {
            _showingTitle = true;
            TitleBox.Text = note.Title;
            _showingTitle = false;
        }
        if (note.Id != _editor.NoteId) _editor.Load(note);
        UpdateInfo();
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (_showingTitle || _notes.CurrentNote is not { } note || TitleBox.Text == note.Title) return;
        _notes.SaveTitle(TitleBox.Text);
    }

    /// <summary>Return in the title moves on to the note, as on the Mac.</summary>
    private void OnTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _editor.Focus();
    }

    // MARK: - Footer

    /// <summary>The footer's readout (Mac: FooterView.infoLabel). Click to move to the next.</summary>
    private void UpdateInfo()
    {
        if (_notes.CurrentNote is not { } note) return;
        var text = _editor.Content.CopyText;
        InfoText.Text = _settings.Value.FooterInfo switch
        {
            FooterInfo.Created => $"Created: {TimestampText.Format(note.CreatedAt, DateTimeOffset.Now, TimeZoneInfo.Local)}",
            FooterInfo.Characters => $"{text.Length} characters",
            FooterInfo.Words => $"{text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length} words",
            _ => $"Last edited: {TimestampText.Format(note.UpdatedAt, DateTimeOffset.Now, TimeZoneInfo.Local)}",
        };
        ToolTipService.SetToolTip(InfoButton, _settings.Value.FooterInfo switch
        {
            FooterInfo.LastEdited => "Click to see creation time",
            FooterInfo.Created => "Click to see character count",
            FooterInfo.Characters => "Click to see word count",
            _ => "Click to see last edited time",
        });
    }

    private void OnInfo(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.FooterInfo = (FooterInfo)(((int)s.FooterInfo + 1) % Enum.GetValues<FooterInfo>().Length));
        // The new readout crossfades in (Mac: 0.12s ease-in-out).
        Motion.SetOpacity(InfoText, 0);
        Motion.Fade(InfoText, 1, Motion.Crossfade);
    }

    private void CopyNote()
    {
        var package = new DataPackage();
        package.SetText(_editor.Content.CopyText.Replace("\n", "\r\n"));
        Clipboard.SetContent(package);
        ShowToast("Copied to clipboard");
    }

    private void DeleteCurrentNote()
    {
        if (_notes.CurrentNote is { } note) DeleteNote(note);
    }

    /// <summary>Deletes after asking, as the Mac does; the last note can't go.</summary>
    private async void DeleteNote(Note note)
    {
        if (_notes.IsReadOnly) return;
        if (_notes.Notes.Count <= 1)
        {
            ShowToast("Can't delete your only note", "");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = $"Delete “{(string.IsNullOrWhiteSpace(note.Title) ? "Untitled" : note.Title)}”?",
            Content = "This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            _notes.DeleteNote(note.Id);
        if (!_allNotes.IsOpen) _editor.Focus();
    }

    // MARK: - Toasts (Mac: NotificationToast)

    /// <summary>A short message above the footer. The glyph defaults to a checkmark.</summary>
    public void ShowToast(string text, string glyph = "")
    {
        ToastText.Text = text;
        ToastIcon.Glyph = glyph;
        Toast.UpdateLayout(); // the pop scales about its bottom centre, which depends on the new text
        // Grows up out of the footer with a small overshoot (Mac: BuoyMotion.pillPop + pillAnimation).
        Motion.SetOpacity(Toast, 0);
        Motion.PopIn(Toast, from: 0.5f, anchor: new Vector2(0.5f, 1), response: 0.34, damping: 0.68);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Motion.PopOut(Toast, to: 0.8f, anchor: new Vector2(0.5f, 1));
    }

    /// <summary>Shows a lasting message above the note, such as a hotkey that couldn't be registered.</summary>
    public void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    // MARK: - Links

    private void ShowLinkDialog()
    {
        if (_notes.IsReadOnly) return;
        var (selected, existing) = _editor.LinkContext();
        var text = new TextBox { Header = "Text", Text = selected, PlaceholderText = "Link text" };
        var url = new TextBox { Header = "Link", Text = existing ?? "", PlaceholderText = "example.com" };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            Text = "Enter a web address, like example.com",
        };
        var flyout = new Flyout { ShouldConstrainToRootBounds = false };

        void Insert()
        {
            if (LinkDestination.Normalize(url.Text) is not { } destination)
            {
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            _editor.SetLink(string.IsNullOrWhiteSpace(text.Text) ? destination : text.Text, destination);
        }

        var insert = new Button { Content = existing is null ? "Insert" : "Update", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        insert.Click += (_, _) => Insert();
        url.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Insert(); } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (existing is not null)
        {
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { flyout.Hide(); _editor.RemoveLink(); };
            buttons.Children.Add(remove);
        }
        buttons.Children.Add(insert);

        // A fixed width: a flyout sizes to its content, and a text box's ideal width is its placeholder's.
        flyout.Content = new StackPanel { Width = 260, Spacing = 10, Children = { text, url, error, buttons } };
        flyout.Opened += (_, _) => (string.IsNullOrEmpty(selected) ? text : url).Focus(FocusState.Programmatic);
        flyout.ShowAt(LinkButton);
    }

    // MARK: - Commands

    /// <summary>
    /// Buoy's shortcuts, taken at the root before the editor sees them (RichEdit
    /// would otherwise keep Ctrl+Enter, Ctrl+K and others for itself).
    /// </summary>
    private void OnShortcut(object sender, KeyRoutedEventArgs e)
    {
        bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape && _allNotes.IsOpen)
        {
            // The search field clears itself first; anywhere else, Escape closes the list.
            if (!_allNotes.IsSearchField(e.OriginalSource))
            {
                e.Handled = true;
                _allNotes.Close();
            }
            return;
        }
        if (!Down(VirtualKey.Control) || Down(VirtualKey.Menu)) return;
        var shift = Down(VirtualKey.Shift);

        Action? command = (e.Key, shift) switch
        {
            (VirtualKey.N, false) => NewNote,
            (VirtualKey.L, false) => _allNotes.Toggle,
            (VirtualKey.W, false) => HidePanel,
            (VirtualKey.Enter, false) => CopyNote,
            (VirtualKey.K, false) => ShowLinkDialog,
            (VirtualKey.D, false) => DeleteCurrentNote,
            (VirtualKey.PageUp, false) => () => SlideToNote(forward: false),
            (VirtualKey.PageDown, false) => () => SlideToNote(forward: true),
            ((VirtualKey)0xBC, false) => () => SettingsButton.Flyout.ShowAt(SettingsButton), // Ctrl+,
            _ => null,
        };
        if (command is null) return;
        e.Handled = true;
        command();
    }

    private void NewNote()
    {
        if (_notes.IsReadOnly) return;
        _notes.CreateNote();
        _editor.Focus();
    }

    private void OnNewNote(object sender, RoutedEventArgs e) => NewNote();
    private void OnAllNotes(object sender, RoutedEventArgs e) => _allNotes.Toggle();
    private void OnCopy(object sender, RoutedEventArgs e) => CopyNote();
    private void OnDeleteNote(object sender, RoutedEventArgs e) => DeleteCurrentNote();
    private void OnLink(object sender, RoutedEventArgs e) => ShowLinkDialog();
    private void OnBold(object sender, RoutedEventArgs e) => _editor.ToggleBold();
    private void OnItalic(object sender, RoutedEventArgs e) => _editor.ToggleItalic();
    private void OnUnderline(object sender, RoutedEventArgs e) => _editor.ToggleUnderline();
    private void OnStrikethrough(object sender, RoutedEventArgs e) => _editor.ToggleStrikethrough();
    private void OnBullets(object sender, RoutedEventArgs e) => _editor.ToggleBullets();
    private void OnTodos(object sender, RoutedEventArgs e) => _editor.ToggleTodos();
}
