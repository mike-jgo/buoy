using System.Text;
using Buoy.Core.Documents;
using Microsoft.UI.Text;

namespace Buoy.Windows.Editor;

/// <summary>
/// Moves notes between <c>contentRTF</c>, a RichEdit document and
/// <see cref="BuoyDoc"/>. See <c>docs/plans/windows-mvp.md</c> → "RTF".
/// </summary>
public static class RichEditConversion
{
    /// <summary>
    /// Loads stored RTF as-is. The blob is 7-bit ASCII from both apps; Latin-1
    /// keeps any stray high byte as the same byte rather than dropping it.
    /// </summary>
    public static void LoadRtf(RichEditTextDocument document, byte[] rtf)
    {
        if (rtf.Length == 0)
            document.SetText(TextSetOptions.None, "");
        else
            document.SetText(TextSetOptions.FormatRtf, Encoding.Latin1.GetString(rtf));
    }

    public static BuoyDoc Read(RichEditTextDocument document)
    {
        var story = document.GetRange(0, 0);
        story.Expand(TextRangeUnit.Story);
        return new BuoyDoc(ReadParagraphs(document, 0, story.EndPosition - 1, out _, out _));
    }

    /// <summary>
    /// The whole paragraphs that overlap [<paramref name="from"/>, <paramref name="to"/>],
    /// and where they start and end (the end is before the last one's paragraph mark).
    /// </summary>
    public static List<BuoyBlock> ReadParagraphs(RichEditTextDocument document, int from, int to, out int start, out int end)
    {
        var blocks = new List<BuoyBlock>();
        start = end = from;
        var position = from;
        var first = true;
        do
        {
            var paragraph = document.GetRange(position, position);
            paragraph.Expand(TextRangeUnit.Paragraph);
            if (first) start = paragraph.StartPosition;
            first = false;
            blocks.Add(ReadParagraph(document, paragraph));
            paragraph.GetText(TextGetOptions.None, out var text);
            end = text.EndsWith('\r') ? paragraph.EndPosition - 1 : paragraph.EndPosition;
            if (paragraph.EndPosition <= position) break;
            position = paragraph.EndPosition;
        } while (position <= to);
        return blocks;
    }

    private static BuoyBlock ReadParagraph(RichEditTextDocument document, ITextRange paragraph)
    {
        paragraph.GetText(TextGetOptions.None, out var text);
        // Every RichEdit paragraph ends in a paragraph mark, the story's last one included.
        var contentEnd = text.EndsWith('\r') ? paragraph.EndPosition - 1 : paragraph.EndPosition;

        var (type, isChecked, markerLength) = ListMarkers.Detect(VisibleText(document, paragraph.StartPosition, contentEnd));
        var level = 0;
        if (type != BlockType.Paragraph)
        {
            var format = paragraph.ParagraphFormat;
            level = ListMarkers.LevelFromFirstLineIndent(format.LeftIndent + format.FirstLineIndent);
        }

        var runs = ReadRuns(document, paragraph.StartPosition, contentEnd, skipVisible: markerLength);
        return new BuoyBlock(type, runs, level, isChecked);
    }

    private static string VisibleText(RichEditTextDocument document, int start, int end)
    {
        var text = new StringBuilder();
        ForEachRun(document, start, end, (range, runText) =>
        {
            if (range.CharacterFormat.Hidden != FormatEffect.On) text.Append(runText);
        });
        return text.ToString();
    }

    private static List<BuoyRun> ReadRuns(RichEditTextDocument document, int start, int end, int skipVisible)
    {
        var runs = new List<BuoyRun>();
        ForEachRun(document, start, end, (range, runText) =>
        {
            var format = range.CharacterFormat;
            // Link instructions are stored as hidden text in front of the link's label.
            if (format.Hidden == FormatEffect.On) return;

            if (skipVisible > 0)
            {
                var skip = Math.Min(skipVisible, runText.Length);
                runText = runText[skip..];
                skipVisible -= skip;
            }

            runText = Sanitize(runText);
            if (runText.Length == 0) return;

            runs.Add(new BuoyRun(
                runText,
                Bold: format.Bold == FormatEffect.On,
                Italic: format.Italic == FormatEffect.On,
                Underline: format.Underline is not (UnderlineType.None or UnderlineType.Undefined),
                Strikethrough: format.Strikethrough == FormatEffect.On,
                Link: LinkTarget(range)));
        });
        return runs;
    }

    /// <summary>Calls <paramref name="visit"/> for each run of uniform character formatting in [start, end).</summary>
    private static void ForEachRun(RichEditTextDocument document, int start, int end, Action<ITextRange, string> visit)
    {
        var position = start;
        while (position < end)
        {
            var range = document.GetRange(position, position);
            range.Expand(TextRangeUnit.CharacterFormat);
            var runEnd = Math.Min(range.EndPosition, end);
            if (runEnd <= position) runEnd = position + 1;
            range.SetRange(position, runEnd);
            range.GetText(TextGetOptions.None, out var text);
            visit(range, text);
            position = runEnd;
        }
    }

    /// <summary>
    /// RichEdit reports a link target wrapped in quotes (<c>"https://…"</c>).
    /// Targets the format doesn't allow are dropped; the text stays.
    /// </summary>
    private static string? LinkTarget(ITextRange range)
    {
        var link = range.Link;
        if (string.IsNullOrEmpty(link)) return null;
        link = link.Trim().Trim('"');
        return BuoyDoc.IsAllowedLink(link) ? link : null;
    }

    /// <summary>
    /// BuoyDoc runs hold one paragraph of plain characters: soft line breaks
    /// become spaces, and object placeholders and other controls are dropped.
    /// </summary>
    private static string Sanitize(string text)
    {
        const char lineSeparator = (char)0x2028; // as a literal, C# reads it as a line break
        const char objectReplacement = (char)0xFFFC;
        var result = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\v' or '\n' or '\r' or lineSeparator) result.Append(' ');
            else if (ch == '\t' || (ch >= ' ' && ch != objectReplacement)) result.Append(ch);
        }
        return result.ToString();
    }
}
