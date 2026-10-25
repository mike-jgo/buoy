using System.Globalization;
using System.Text;

namespace Buoy.Core.Documents;

/// <summary>
/// How the Windows editor shows a document: Buoy's font at the user's size,
/// to-do markers in a symbol font and followed by a space, and wrapped list
/// lines hanging under their text. Hangs are the width of a marker and its
/// space, in points.
/// </summary>
public sealed record EditorRtfStyle(
    double FontSizePoints,
    double BulletHang,
    double TodoHang,
    string FontName = "Segoe UI",
    string SymbolFontName = "Segoe UI Symbol");

/// <summary>
/// Writes a <see cref="BuoyDoc"/> as RTF.
///
/// <see cref="Write"/> is the stored form in <c>notes.contentRTF</c>: one small
/// dialect that the Mac's Cocoa reader and Windows' RichEdit both read.
/// Standard control words only, 7-bit ASCII with <c>\uN?</c> escapes, one font,
/// no colour table, no spacing; every reader normalises those on load. List
/// paragraphs carry their level as the first-line indent (<c>\li</c> = level ×
/// 20pt, <c>\fi0</c>) and no hang, which both apps recompute on load.
///
/// <see cref="WriteEditorString"/> is what the Windows editor loads: the same
/// document laid out in <see cref="EditorRtfStyle"/>. Loading through it is
/// also how the editor drops a note's stray fonts and colours.
/// </summary>
public static class RtfWriter
{
    private const int TwipsPerPoint = 20;

    public static byte[] Write(BuoyDoc doc) => Encoding.ASCII.GetBytes(WriteString(doc));

    public static string WriteString(BuoyDoc doc) => doc.Blocks.Count == 0 ? "" : Write(doc, style: null);

    public static string WriteEditorString(BuoyDoc doc, EditorRtfStyle style) => Write(doc, style);

    private static string Write(BuoyDoc doc, EditorRtfStyle? style)
    {
        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil ");
        AppendText(rtf, style?.FontName ?? "Segoe UI");
        rtf.Append(";}");
        if (style is not null)
        {
            rtf.Append(@"{\f1\fnil ");
            AppendText(rtf, style.SymbolFontName);
            rtf.Append(";}");
        }
        rtf.Append("}\n");

        // The editor always has a paragraph, so an empty note still carries the font and size.
        var blocks = doc.Blocks.Count == 0 ? [BuoyBlock.Paragraph("")] : doc.Blocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0) rtf.Append("\\par\n");
            WriteBlock(rtf, blocks[i], style);
        }

        rtf.Append('}');
        return rtf.ToString();
    }

    private static void WriteBlock(StringBuilder rtf, BuoyBlock block, EditorRtfStyle? style)
    {
        var halfPoints = style is null ? 28 : (int)Math.Round(style.FontSizePoints * 2);
        rtf.Append(CultureInfo.InvariantCulture, $@"\pard\plain\f0\fs{halfPoints}");
        if (block.Type != BlockType.Paragraph)
        {
            var hang = style is null ? 0 : block.Type == BlockType.Todo ? style.TodoHang : style.BulletHang;
            var left = Twips(block.Level * ListMarkers.IndentWidth + hang);
            rtf.Append(CultureInfo.InvariantCulture, $@"\li{left}\fi{-Twips(hang)}");
        }
        rtf.Append(' ');

        if (style is not null && block.Type == BlockType.Todo)
        {
            rtf.Append(@"{\f1 ");
            AppendText(rtf, ListMarkers.TodoFor(block.Checked).ToString());
            rtf.Append("} ");
        }
        else
        {
            AppendText(rtf, ListMarkers.StoredPrefix(block));
        }

        foreach (var run in block.Runs)
            WriteRun(rtf, run);
    }

    private static int Twips(double points) => (int)Math.Round(points * TwipsPerPoint);

    private static void WriteRun(StringBuilder rtf, BuoyRun run)
    {
        var flags = new StringBuilder();
        if (run.Bold) flags.Append(@"\b");
        if (run.Italic) flags.Append(@"\i");
        if (run.Underline) flags.Append(@"\ul");
        if (run.Strikethrough) flags.Append(@"\strike");

        if (run.Link is not null)
        {
            rtf.Append(@"{\field{\*\fldinst{HYPERLINK """);
            AppendText(rtf, run.Link.Replace("\"", "%22"));
            rtf.Append(@"""}}{\fldrslt{");
            // The space ends the last control word; without flags it would be text.
            if (flags.Length > 0) rtf.Append(flags).Append(' ');
            AppendText(rtf, run.Text);
            rtf.Append("}}}");
            return;
        }

        if (flags.Length == 0)
        {
            AppendText(rtf, run.Text);
            return;
        }

        rtf.Append('{').Append(flags).Append(' ');
        AppendText(rtf, run.Text);
        rtf.Append('}');
    }

    /// <summary>
    /// Escapes text for RTF. Everything outside printable ASCII becomes
    /// <c>\uN?</c>, one per UTF-16 unit (so emoji are two), with N signed as RTF
    /// requires.
    /// </summary>
    private static void AppendText(StringBuilder rtf, string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': rtf.Append(@"\\"); break;
                case '{': rtf.Append(@"\{"); break;
                case '}': rtf.Append(@"\}"); break;
                case '\t': rtf.Append(@"\tab "); break;
                case < ' ': break;
                case > '~':
                    rtf.Append(CultureInfo.InvariantCulture, $@"\u{(short)ch}?");
                    break;
                default: rtf.Append(ch); break;
            }
        }
    }
}
