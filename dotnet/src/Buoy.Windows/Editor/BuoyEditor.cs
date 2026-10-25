using Buoy.Core.Documents;
using Buoy.Core.Storage;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Buoy.Windows.Editor;

/// <summary>
/// Buoy's editing behaviour on a RichEditBox, after the Mac's
/// <c>BuoyTextView</c>: <c>-</c>/<c>[]</c> + Space start lists, Return continues
/// or ends them, Backspace after a marker removes it, Tab/Shift+Tab nest them
/// (0–2), clicking a to-do toggles it, wrapped list lines hang under their text.
/// The rules themselves are <see cref="ListRules"/>.
///
/// Every edit is saved to <see cref="NoteStore"/> under the id of the note that
/// was loaded, never "the current note" (see <c>CLAUDE.md</c> → the debounced
/// save bug).
/// </summary>
internal sealed class BuoyEditor
{
    public const string FontName = "Segoe UI";
    public const string SymbolFontName = "Segoe UI Symbol";

    private readonly RichEditBox _box;
    private readonly NoteStore _notes;
    private EditorRtfStyle _style;
    private bool _loading;

    /// <summary>The note in the editor; edits are saved to it.</summary>
    public string? NoteId { get; private set; }

    /// <summary>The note as last read from the editor, for the footer's counts and Copy.</summary>
    public BuoyDoc Content { get; private set; } = BuoyDoc.Empty;

    public event EventHandler? ContentChanged;

    private RichEditTextDocument Document => _box.Document;

    public BuoyEditor(RichEditBox box, NoteStore notes, double fontSize)
    {
        _box = box;
        _notes = notes;
        _style = StyleFor(fontSize);

        // Buoy owns Ctrl+B/I/U (and blocks RichEdit's other formatting keys in OnPreviewKeyDown).
        _box.DisabledFormattingAccelerators = DisabledFormattingAccelerators.All;
        _box.FontFamily = new FontFamily(FontName);
        _box.FontSize = fontSize;
        _box.PlaceholderText = "Start typing…";
        _box.TextWrapping = TextWrapping.Wrap;

        _box.TextChanged += (_, _) => Save();
        _box.ActualThemeChanged += (_, _) => OnThemeChanged();
        _box.PreviewKeyDown += OnPreviewKeyDown;
        _box.Paste += OnPaste;
        _box.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
    }

    // MARK: - Loading and saving

    /// <summary>Shows <paramref name="note"/>, laid out in Buoy's editor style, with a fresh undo history.</summary>
    public void Load(Note note)
    {
        NoteId = note.Id;
        _box.IsReadOnly = false; // RichEdit refuses SetText while read-only
        _loading = true;
        try
        {
            // Through BuoyDoc and back: drops the note's stored fonts and colours,
            // adds the symbol font for to-dos and the hanging indents.
            RichEditConversion.LoadRtf(Document, note.ContentRtf);
            var doc = RichEditConversion.Read(Document);
            Content = doc;
            Document.SetText(TextSetOptions.FormatRtf, RtfWriter.WriteEditorString(doc, _style));
            ApplyTextColor();
            Document.ClearUndoRedoHistory();
            var end = StoryEnd();
            Document.Selection.SetRange(end, end);
        }
        finally
        {
            _loading = false;
        }
        _box.IsReadOnly = _notes.IsReadOnly;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-lays the note out at a new font size, keeping the caret.</summary>
    public void SetFontSize(double fontSize)
    {
        _style = StyleFor(fontSize);
        _box.FontSize = fontSize;
        if (NoteId is null) return;

        var caret = Document.Selection.StartPosition;
        var doc = RichEditConversion.Read(Document);
        _loading = true;
        try
        {
            Document.SetText(TextSetOptions.FormatRtf, RtfWriter.WriteEditorString(doc, _style));
            ApplyTextColor();
            Document.Selection.SetRange(Math.Min(caret, StoryEnd()), Math.Min(caret, StoryEnd()));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Colours the whole note with the theme's text colour. Loaded RTF carries
    /// no colour, and RichEdit's "automatic" colour is black even in dark mode,
    /// so loaded text was near-invisible while typed text was fine. Colour is
    /// never stored: <see cref="RichEditConversion.Read"/> ignores it.
    /// </summary>
    private void ApplyTextColor() =>
        Document.GetRange(0, StoryEnd() + 1).CharacterFormat.ForegroundColor = TextColor();

    private global::Windows.UI.Color TextColor() => _box.ActualTheme == ElementTheme.Dark
        ? global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)  // TextFillColorPrimary, dark
        : global::Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00); // TextFillColorPrimary, light

    private void OnThemeChanged()
    {
        var wasLoading = _loading;
        _loading = true;
        try { ApplyTextColor(); }
        finally { _loading = wasLoading; }
    }

    private void Save()
    {
        if (_loading || NoteId is null || _box.IsReadOnly) return;
        var doc = RichEditConversion.Read(Document);
        if (doc.Equals(Content)) return; // a selection or caret move, not an edit
        Content = doc;
        _notes.SaveContent(NoteId, RtfWriter.Write(doc), doc);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - Links

    /// <summary>The selected text and the link already on it, to pre-fill the link dialog.</summary>
    public (string Text, string? Url) LinkContext()
    {
        var selection = Document.Selection;
        selection.GetText(TextGetOptions.None, out var text);
        var link = selection.Link?.Trim().Trim('"');
        return (text.TrimEnd('\r'), string.IsNullOrEmpty(link) ? null : link);
    }

    /// <summary>Links the selection to <paramref name="url"/>, typing <paramref name="text"/> first if nothing is selected.</summary>
    public void SetLink(string text, string url)
    {
        if (_box.IsReadOnly) return;
        Edit(() =>
        {
            var selection = Document.Selection;
            selection.GetText(TextGetOptions.None, out var current);
            if (selection.Length == 0 || current != text)
                selection.SetText(TextSetOptions.None, text);
            selection.Link = $"\"{url}\"";
            Caret(selection.EndPosition);
        });
        _box.Focus(FocusState.Programmatic);
    }

    public void RemoveLink()
    {
        if (_box.IsReadOnly) return;
        Edit(() => Document.Selection.Link = "");
        _box.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Marker widths in points, measured in the editor's own fonts so a wrapped
    /// line starts exactly under the first letter of the item.
    /// </summary>
    private static EditorRtfStyle StyleFor(double fontSizeDips)
    {
        const double pointsPerDip = 0.75;
        double Width(string text, string font)
        {
            var block = new TextBlock { Text = text, FontFamily = new FontFamily(font), FontSize = fontSizeDips };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }
        var space = Width("x x", FontName) - Width("xx", FontName);
        return new EditorRtfStyle(
            FontSizePoints: fontSizeDips * pointsPerDip,
            BulletHang: (Width($"{ListMarkers.Bullet}", FontName) + space) * pointsPerDip,
            TodoHang: (Width($"{ListMarkers.Unchecked}", SymbolFontName) + space) * pointsPerDip,
            FontName: FontName,
            SymbolFontName: SymbolFontName);
    }

    // MARK: - Keys

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_box.IsReadOnly) return;
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);

        if (ctrl && !alt)
        {
            e.Handled = HandleControlKey(e.Key, shift);
            return;
        }
        if (alt) return;

        e.Handled = e.Key switch
        {
            VirtualKey.Space when !shift => AutoComplete(),
            VirtualKey.Enter when !shift => Return(),
            VirtualKey.Back => Backspace(),
            VirtualKey.Tab => Tab(shift),
            _ => false,
        };
    }

    private bool HandleControlKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.B when !shift: ToggleBold(); return true;
            case VirtualKey.I when !shift: ToggleItalic(); return true;
            case VirtualKey.U when !shift: ToggleUnderline(); return true;
            case VirtualKey.X when shift: ToggleStrikethrough(); return true;
            // RichEdit's own shortcuts for alignment, line spacing, list styles,
            // capitals, sub/superscript and font size: none of them survive a
            // save, so they must not change the document.
            case VirtualKey.E or VirtualKey.R or VirtualKey.J or VirtualKey.L
                or VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number5
                or (VirtualKey)0xBB /* = */ or (VirtualKey)0xBE /* > */ or (VirtualKey)0xBC /* < */:
                return true;
            case VirtualKey.A when shift:
                return true;
            default:
                return false;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Space after <c>-</c> or <c>[]</c> at the start of a line starts a list.</summary>
    private bool AutoComplete()
    {
        var selection = Document.Selection;
        if (selection.Length != 0) return false;
        var (paragraph, before) = CaretParagraph();
        if (ListRules.AutoComplete(before) is not { } type) return false;

        var level = Level(paragraph);
        Edit(() =>
        {
            Document.GetRange(paragraph.StartPosition, selection.StartPosition).SetText(TextSetOptions.None, "");
            InsertMarker(paragraph.StartPosition, type, level, isChecked: false);
            SetListFormat(paragraph.StartPosition, type, level);
            Caret(paragraph.StartPosition + 2);
        });
        return true;
    }

    private bool Return()
    {
        var selection = Document.Selection;
        if (selection.Length != 0) return false;
        var (paragraph, before) = CaretParagraph();

        switch (ListRules.OnReturn(before, Level(paragraph)))
        {
            case ReturnOutcome.Continue(var marker, var level):
                Edit(() =>
                {
                    var caret = selection.StartPosition;
                    Document.GetRange(caret, caret).SetText(TextSetOptions.None, "\r");
                    var type = ListMarkers.Detect(marker).Type;
                    InsertMarker(caret + 1, type, level, isChecked: false);
                    SetListFormat(caret + 1, type, level);
                    Caret(caret + 1 + marker.Length);
                });
                return true;

            case ReturnOutcome.EndList(var markerLength, _):
                // Return on an empty item leaves the list: the marker and indent go, no new line.
                Edit(() => EndList(paragraph.StartPosition, markerLength));
                return true;

            default:
                return false;
        }
    }

    private bool Backspace()
    {
        var selection = Document.Selection;
        if (selection.Length != 0) return false;
        var (paragraph, before) = CaretParagraph();
        if (ListRules.OnBackspace(before) is not BackspaceOutcome.RemoveMarker(var markerLength, _)) return false;

        Edit(() => EndList(paragraph.StartPosition, markerLength));
        return true;
    }

    private bool Tab(bool shift)
    {
        var (paragraph, _) = CaretParagraph();
        var (type, _, _) = ListMarkers.Detect(ParagraphText(paragraph));
        if (ListRules.OnTab(type, Level(paragraph), shift) is not { } level) return false;

        Edit(() =>
        {
            if (type == BlockType.Bullet)
            {
                var marker = Document.GetRange(paragraph.StartPosition, paragraph.StartPosition + 1);
                marker.SetText(TextSetOptions.None, ListMarkers.BulletFor(level).ToString());
            }
            SetListFormat(paragraph.StartPosition, type, level);
        });
        return true;
    }

    /// <summary>Removes a list marker and the indent with it; the line becomes plain text.</summary>
    private void EndList(int paragraphStart, int markerLength)
    {
        Document.GetRange(paragraphStart, paragraphStart + markerLength).SetText(TextSetOptions.None, "");
        Document.GetRange(paragraphStart, paragraphStart).ParagraphFormat.SetIndents(0, 0, 0);
        // Text typed next starts plain, not in the symbol font of a removed to-do.
        Document.Selection.CharacterFormat.Name = FontName;
        Caret(paragraphStart);
    }

    // MARK: - List helpers

    private (ITextRange Paragraph, string TextBeforeCaret) CaretParagraph()
    {
        var caret = Document.Selection.StartPosition;
        var paragraph = Document.GetRange(caret, caret);
        paragraph.Expand(TextRangeUnit.Paragraph);
        Document.GetRange(paragraph.StartPosition, caret).GetText(TextGetOptions.None, out var before);
        return (paragraph, before);
    }

    private static string ParagraphText(ITextRange paragraph)
    {
        paragraph.GetText(TextGetOptions.None, out var text);
        return text.TrimEnd('\r');
    }

    private static int Level(ITextRange paragraph)
    {
        var format = paragraph.ParagraphFormat;
        return ListMarkers.LevelFromFirstLineIndent(format.LeftIndent + format.FirstLineIndent);
    }

    /// <summary>Writes a marker and its space at <paramref name="position"/>; a to-do's box is in the symbol font.</summary>
    private void InsertMarker(int position, BlockType type, int level, bool isChecked)
    {
        var marker = type == BlockType.Todo ? ListMarkers.TodoFor(isChecked) : ListMarkers.BulletFor(level);
        Document.GetRange(position, position).SetText(TextSetOptions.None, $"{marker} ");
        var glyph = Document.GetRange(position, position + 1).CharacterFormat;
        glyph.Name = type == BlockType.Todo ? SymbolFontName : FontName;
        glyph.Bold = FormatEffect.Off;
        glyph.Italic = FormatEffect.Off;
        glyph.Underline = UnderlineType.None;
        glyph.Strikethrough = FormatEffect.Off;
    }

    /// <summary>First line at the level; wrapped lines hang under the item's text, as on the Mac.</summary>
    private void SetListFormat(int position, BlockType type, int level)
    {
        var hang = type switch
        {
            BlockType.Bullet => _style.BulletHang,
            BlockType.Todo => _style.TodoHang,
            _ => 0,
        };
        var left = (float)(level * ListMarkers.IndentWidth + hang);
        Document.GetRange(position, position).ParagraphFormat.SetIndents((float)-hang, left, 0);
    }

    private void Caret(int position) => Document.Selection.SetRange(position, position);

    private int StoryEnd()
    {
        var story = Document.GetRange(0, 0);
        story.Expand(TextRangeUnit.Story);
        return Math.Max(0, story.EndPosition - 1);
    }

    /// <summary>Runs a structural edit as one undo step, then saves.</summary>
    private void Edit(Action change)
    {
        Document.BeginUndoGroup();
        try { change(); }
        finally { Document.EndUndoGroup(); }
        Save();
    }

    // MARK: - Formatting (Ctrl+B/I/U, Ctrl+Shift+X and the toolbar)

    public void ToggleBold() => Format(f => f.Bold = FormatEffect.Toggle);
    public void ToggleItalic() => Format(f => f.Italic = FormatEffect.Toggle);
    public void ToggleStrikethrough() => Format(f => f.Strikethrough = FormatEffect.Toggle);
    public void ToggleUnderline() =>
        Format(f => f.Underline = f.Underline == UnderlineType.Single ? UnderlineType.None : UnderlineType.Single);

    private void Format(Action<ITextCharacterFormat> change)
    {
        if (_box.IsReadOnly) return;
        var format = Document.Selection.CharacterFormat;
        change(format);
        Save();
    }

    public void ToggleBullets() => ToggleList(BlockType.Bullet);
    public void ToggleTodos() => ToggleList(BlockType.Todo);

    /// <summary>
    /// Turns every line in the selection into <paramref name="target"/> items,
    /// or back into plain lines when they all already are.
    /// </summary>
    private void ToggleList(BlockType target)
    {
        if (_box.IsReadOnly) return;
        var selection = Document.Selection;
        var paragraphs = new List<(int Start, string Text, int Level)>();
        var position = selection.StartPosition;
        do
        {
            var paragraph = Document.GetRange(position, position);
            paragraph.Expand(TextRangeUnit.Paragraph);
            paragraphs.Add((paragraph.StartPosition, ParagraphText(paragraph), Level(paragraph)));
            if (paragraph.EndPosition <= position) break;
            position = paragraph.EndPosition;
        } while (position < selection.EndPosition && position < StoryEnd());

        var removing = paragraphs.All(p => ListMarkers.Detect(p.Text).Type == target);
        Edit(() =>
        {
            // From the bottom up, so earlier paragraphs keep their positions.
            foreach (var (start, text, level) in Enumerable.Reverse(paragraphs))
            {
                var (type, _, markerLength) = ListMarkers.Detect(text);
                if (type != BlockType.Paragraph)
                    Document.GetRange(start, start + markerLength).SetText(TextSetOptions.None, "");
                if (removing)
                {
                    Document.GetRange(start, start).ParagraphFormat.SetIndents(0, 0, 0);
                    continue;
                }
                InsertMarker(start, target, level, isChecked: false);
                SetListFormat(start, target, level);
            }
        });
        _box.Focus(FocusState.Programmatic);
    }

    // MARK: - Pointer: to-do boxes and links

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // RichEdit's client coordinates are those of the text area inside the box's template.
        var textArea = _textArea ??= FindByName(_box, "ContentElement") ?? _box;
        var point = e.GetCurrentPoint(textArea);
        if (!point.Properties.IsLeftButtonPressed) return;
        var scale = _box.XamlRoot?.RasterizationScale ?? 1;
        var pixel = new Point(point.Position.X * scale, point.Position.Y * scale);
        var hit = Document.GetRangeFromPoint(pixel, PointOptions.ClientCoordinates);

        if (IsDown(VirtualKey.Control) && OpenLink(hit)) return;
        if (!_box.IsReadOnly) ToggleTodoAt(hit.StartPosition, pixel);
    }

    private UIElement? _textArea;

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

    /// <summary>
    /// Toggles the to-do whose box is under <paramref name="pixel"/>. The box is the
    /// first character of its line; a click on it, or on the space after it, can
    /// hit-test to the box, the space or the first letter.
    /// </summary>
    private void ToggleTodoAt(int position, Point pixel)
    {
        foreach (var candidate in new[] { position, position - 1, position - 2 })
        {
            if (candidate < 0) continue;
            var paragraph = Document.GetRange(candidate, candidate);
            paragraph.Expand(TextRangeUnit.Paragraph);
            if (paragraph.StartPosition != candidate) continue;

            var box = Document.GetRange(candidate, candidate + 1);
            box.GetText(TextGetOptions.None, out var glyph);
            if (glyph.Length != 1 || glyph[0] is not (ListMarkers.Unchecked or ListMarkers.Checked)) continue;

            // Only a click on the box or the space after it, not anywhere on the line.
            var boxAndSpace = Document.GetRange(candidate, candidate + 2);
            boxAndSpace.GetRect(PointOptions.ClientCoordinates, out var rect, out _);
            if (pixel.X < rect.Left - 2 || pixel.X > rect.Right
                || pixel.Y < rect.Top || pixel.Y > rect.Bottom) return;

            var toggled = glyph[0] == ListMarkers.Unchecked ? ListMarkers.Checked : ListMarkers.Unchecked;
            Edit(() => box.SetText(TextSetOptions.None, toggled.ToString()));
            return;
        }
    }

    /// <summary>Ctrl+click opens a link, as in other Windows editors.</summary>
    private static bool OpenLink(ITextRange hit)
    {
        var link = hit.Link?.Trim().Trim('"');
        if (!BuoyDoc.IsAllowedLink(link) || !Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        _ = Launcher.LaunchUriAsync(uri);
        return true;
    }

    // MARK: - Paste

    /// <summary>
    /// Lets RichEdit paste natively (synchronous, so keys typed right after
    /// Ctrl+V stay in order), then rebuilds the pasted lines through BuoyDoc on
    /// the next tick: bold, italic, underline, strikethrough and links stay, as
    /// on the Mac; outside fonts, colours, highlights and pictures go; lines
    /// written as <c>- item</c> or <c>* item</c> become bullets.
    /// </summary>
    private void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        if (_box.IsReadOnly) return;
        var start = Document.Selection.StartPosition;
        _box.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => TidyPaste(start));
    }

    private void TidyPaste(int start)
    {
        var caret = Document.Selection.EndPosition;
        if (caret <= start) return;

        var blocks = RichEditConversion.ReadParagraphs(Document, start, caret, out var from, out var to);
        // How far the caret sits from the end of the last pasted line; the text after it doesn't change.
        var caretFromEnd = to - caret;
        var doc = new BuoyDoc(ListRules.PromotePlainBullets(blocks));

        _loading = true; // one save at the end, not one per intermediate state
        try
        {
            Edit(() =>
            {
                var range = Document.GetRange(from, to);
                range.SetText(TextSetOptions.FormatRtf, RtfWriter.WriteEditorString(doc, _style));
                range.CharacterFormat.ForegroundColor = TextColor();
                // The last line keeps its existing paragraph mark, which RTF
                // pasted into a range doesn't restyle; set its indents directly.
                if (doc.Blocks.Count > 0 && doc.Blocks[^1] is var last)
                    SetListFormat(range.EndPosition, last.Type, last.Level);
                Caret(Math.Max(range.StartPosition, range.EndPosition - caretFromEnd));
            });
        }
        finally
        {
            _loading = false;
        }
        Save();
    }

    public void Focus() => _box.Focus(FocusState.Programmatic);
}
