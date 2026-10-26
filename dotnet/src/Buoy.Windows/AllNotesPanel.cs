using System.Numerics;
using Buoy.Core.Storage;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Buoy.Windows;

/// <summary>
/// All Notes, the card that opens over the note (the Mac's <c>AllNotesPanel</c>,
/// without folders: they're kept but not shown in the MVP). A search field,
/// then Pinned and All Notes. The open note is bold on a soft fill; pin and
/// delete appear on hover, and a pinned note always shows its pin. ↑/↓ move a
/// highlight from the search field, Return opens it, Escape clears the search
/// and then closes. Right-click has the same actions.
/// </summary>
internal sealed class AllNotesPanel : UserControl
{
    private readonly Border _card = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
    private const double ListMaxHeight = 300;

    private readonly NoteStore _notes;
    private readonly NotePlainTextCache _text = new();
    private readonly TextBox _search;
    private readonly StackPanel _rows = new() { Padding = new Thickness(4, 0, 4, 6) };
    private readonly TextBlock _empty;
    private readonly List<(Note Note, Border Pill)> _rowPills = [];
    private int _highlight = -1;

    /// <summary>A note was picked; the panel has closed.</summary>
    public event Action<string>? NoteChosen;
    public event Action<Note>? DeleteRequested;
    public event Action? Closed;

    /// <summary>True from the moment it starts opening until it starts closing (not while it fades out).</summary>
    public bool IsOpen { get; private set; }

    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(0.16);

    /// <summary>Escape in the search field clears it before closing; the window asks so it can leave that to the field.</summary>
    public bool IsSearchField(object? element) => ReferenceEquals(element, _search);

    public AllNotesPanel(NoteStore notes)
    {
        _notes = notes;
        Visibility = Visibility.Collapsed;
        VerticalAlignment = VerticalAlignment.Top;
        ApplyThemeBrushes();

        var title = new TextBlock { Text = "All Notes", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = new FontIcon { Glyph = "", FontSize = 8 },
        };
        AutomationProperties.SetName(close, "Close All Notes");
        close.Click += (_, _) => Close();
        var header = new Grid { Padding = new Thickness(10, 8, 8, 8), Children = { title, close } };

        _search = new TextBox { PlaceholderText = "Search notes…", Margin = new Thickness(10, 0, 10, 6) };
        AutomationProperties.SetName(_search, "Search notes");
        _search.TextChanged += (_, _) => Rebuild();
        _search.PreviewKeyDown += OnSearchKey;

        _empty = new TextBlock
        {
            Text = "No matching notes",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 20),
            Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            Visibility = Visibility.Collapsed,
        };
        var list = new ScrollViewer { MaxHeight = ListMaxHeight, Content = _rows };

        var layout = new Grid { RowDefinitions = { new(), new(), new() } };
        layout.RowDefinitions[0].Height = GridLength.Auto;
        layout.RowDefinitions[1].Height = GridLength.Auto;
        Grid.SetRow(_search, 1);
        Grid.SetRow(list, 2);
        Grid.SetRow(_empty, 2);
        layout.Children.Add(header);
        layout.Children.Add(_search);
        layout.Children.Add(list);
        layout.Children.Add(_empty);
        _card.Child = layout;
        // The shadow is a composition drop shadow on a layer behind the card, not
        // ThemeShadow: ThemeShadow needs the card raised with a Z translation, and
        // that shifted its hit-testing a row (32px) above where it was drawn, so
        // hovering or clicking a note landed on the one above it.
        var shadowLayer = new Grid { IsHitTestVisible = false };
        Content = new Grid { Children = { shadowLayer, _card } };
        AttachShadow(shadowLayer);

        _notes.Changed += (_, _) => { if (IsOpen) Rebuild(); };
        // The card's own brushes follow its theme too, from the moment it is in the tree.
        Loaded += (_, _) => ApplyThemeBrushes();
        ActualThemeChanged += (_, _) =>
        {
            ApplyThemeBrushes();
            if (IsOpen) Rebuild();
        };
    }

    private static void AttachShadow(FrameworkElement layer)
    {
        var compositor = Motion.VisualOf(layer).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 24;
        shadow.Opacity = 0.3f;
        shadow.Offset = new Vector3(0, 8, 0);
        var sprite = compositor.CreateSpriteVisual();
        sprite.Shadow = shadow;
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(layer, sprite);
        // Inset by the corner radius, so the square shadow never pokes out past the rounded corners.
        layer.SizeChanged += (_, e) =>
        {
            sprite.Size = new Vector2((float)e.NewSize.Width - 8, (float)e.NewSize.Height - 8);
            sprite.Offset = new Vector3(4, 4, 0);
        };
    }

    private void ApplyThemeBrushes()
    {
        _card.Background = Resource<Brush>("AcrylicInAppFillColorDefaultBrush");
        _card.BorderBrush = Resource<Brush>("SurfaceStrokeColorFlyoutBrush");
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        IsOpen = true;
        _search.Text = "";
        Rebuild();
        // Fades in (Mac: 0.16s ease-out); a fade is fine under reduced motion too.
        // Reopened mid-fade-out, it continues from where it is rather than blinking out first.
        if (Visibility == Visibility.Collapsed) Motion.SetOpacity(this, 0);
        Visibility = Visibility.Visible;
        Motion.Fade(this, 1, FadeDuration);
        _search.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        // Collapse only once faded out, and only if it wasn't reopened meanwhile.
        Motion.Fade(this, 0, FadeDuration, () => { if (!IsOpen) Visibility = Visibility.Collapsed; });
        Closed?.Invoke();
    }

    // MARK: - Rows

    private void Rebuild()
    {
        var searching = !string.IsNullOrWhiteSpace(_search.Text);
        var sections = AllNotesList.Build(_notes.Notes, _search.Text, _text.Of);
        _rows.Children.Clear();
        _rowPills.Clear();

        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].Header is { } label) _rows.Children.Add(SectionHeader(label, ruled: i > 0));
            foreach (var note in sections[i].Notes) _rows.Children.Add(Row(note));
        }

        _empty.Visibility = _rowPills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // While searching, the first match is highlighted so Return opens it.
        _highlight = searching && _rowPills.Count > 0 ? 0 : -1;
        ShowHighlight();
    }

    /// <summary>PINNED / ALL NOTES, with a hairline above all but the first.</summary>
    private FrameworkElement SectionHeader(string label, bool ruled)
    {
        var text = new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 40,
            Foreground = Resource<Brush>("TextFillColorTertiaryBrush"),
            Margin = new Thickness(6, ruled ? 4 : 2, 0, 4),
        };
        if (!ruled) return text;
        var rule = new Rectangle { Height = 1, Fill = Resource<Brush>("DividerStrokeColorDefaultBrush"), Margin = new Thickness(6, 6, 6, 2) };
        return new StackPanel { Children = { rule, text } };
    }

    private FrameworkElement Row(Note note)
    {
        var isActive = note.Id == _notes.CurrentNote?.Id;
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(note.Title) ? "Untitled" : note.Title,
            FontSize = 13,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Resource<Brush>(isActive ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var pin = RowAction(
            note.IsPinned ? "" : "",
            note.IsPinned ? "Unpin note" : "Pin note",
            () => _notes.TogglePin(note.Id));
        if (note.IsPinned) ((FontIcon)pin.Content).Foreground = Resource<Brush>("AccentTextFillColorPrimaryBrush");
        pin.Visibility = note.IsPinned ? Visibility.Visible : Visibility.Collapsed;
        var delete = RowAction("", "Delete note", () => DeleteRequested?.Invoke(note));
        delete.Visibility = Visibility.Collapsed;

        var content = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = { title, pin, delete },
        };
        Grid.SetColumn(pin, 1);
        Grid.SetColumn(delete, 2);

        var restingFill = isActive ? Resource<Brush>("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Colors.Transparent);
        var pill = new Border
        {
            Height = 32,
            Padding = new Thickness(6, 0, 2, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = restingFill,
            Child = content,
        };
        AutomationProperties.SetName(pill, title.Text);

        pill.PointerEntered += (_, _) =>
        {
            pin.Visibility = Visibility.Visible;
            delete.Visibility = Visibility.Visible;
            if (!isActive) pill.Background = Resource<Brush>("SubtleFillColorTertiaryBrush");
        };
        pill.PointerExited += (_, _) =>
        {
            pin.Visibility = note.IsPinned ? Visibility.Visible : Visibility.Collapsed;
            delete.Visibility = Visibility.Collapsed;
            pill.Background = restingFill;
        };
        pill.Tapped += (_, e) =>
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject, pill)) return;
            Choose(note);
        };

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Open", "", () => Choose(note)));
        menu.Items.Add(MenuItem(note.IsPinned ? "Unpin" : "Pin", note.IsPinned ? "" : "", () => _notes.TogglePin(note.Id)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Delete…", "", () => DeleteRequested?.Invoke(note)));
        pill.ContextFlyout = menu;

        _rowPills.Add((note, pill));
        return pill;
    }

    /// <summary>A bare glyph that fills on hover and press, its hit area larger than the glyph (Mac: RowActionButton).</summary>
    private Button RowAction(string glyph, string label, Action action)
    {
        var button = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = glyph, FontSize = 11, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") },
            IsTabStop = false,
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private static bool IsInsideButton(DependencyObject? element, DependencyObject stop)
    {
        for (var current = element; current is not null && current != stop; current = VisualTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    private void Choose(Note note)
    {
        Close();
        NoteChosen?.Invoke(note.Id);
    }

    // MARK: - Keyboard

    private void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                _highlight = Math.Min(_highlight + 1, _rowPills.Count - 1);
                break;
            case VirtualKey.Up:
                _highlight = Math.Max(_highlight - 1, _rowPills.Count > 0 ? 0 : -1);
                break;
            case VirtualKey.Enter:
                if (_highlight >= 0 && _highlight < _rowPills.Count) Choose(_rowPills[_highlight].Note);
                break;
            case VirtualKey.Escape:
                if (_search.Text.Length > 0) _search.Text = "";
                else Close();
                break;
            default:
                return;
        }
        e.Handled = true;
        ShowHighlight();
    }

    /// <summary>The keyboard highlight is an accent ring around the row.</summary>
    private void ShowHighlight()
    {
        for (var i = 0; i < _rowPills.Count; i++)
        {
            var pill = _rowPills[i].Pill;
            pill.BorderBrush = i == _highlight
                ? Resource<Brush>("AccentFillColorDefaultBrush")
                : new SolidColorBrush(Colors.Transparent);
            if (i == _highlight) pill.StartBringIntoView();
        }
    }

    private T Resource<T>(string key) => ThemeBrushes.Get<T>(this, key);
}
