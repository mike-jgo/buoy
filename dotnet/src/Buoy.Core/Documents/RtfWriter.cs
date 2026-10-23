using System.Globalization;
using System.Text;

namespace Buoy.Core.Documents;

/// <summary>
/// Writes a <see cref="BuoyDoc"/> as the RTF stored in <c>notes.contentRTF</c>.
///
/// One small dialect that the Mac's Cocoa reader and Windows' RichEdit both
/// read: standard control words only, 7-bit ASCII with <c>\uN?</c> escapes, one
/// font, no colour table, no spacing. Every reader normalises font, colour and
/// spacing on load, so none are written.
///
/// List paragraphs carry their level as the first-line indent
/// (<c>\li</c> = level × 20pt, <c>\fi0</c>). The hanging indent under the
/// marker is not written: both apps recompute it from the marker's width on
/// load.
/// </summary>
public static class RtfWriter
{
    private const int TwipsPerPoint = 20;

    public static byte[] Write(BuoyDoc doc) => Encoding.ASCII.GetBytes(WriteString(doc));

    public static string WriteString(BuoyDoc doc)
    {
        if (doc.Blocks.Count == 0) return "";

        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil Segoe UI;}}");
        rtf.Append('\n');

        for (var i = 0; i < doc.Blocks.Count; i++)
        {
            if (i > 0) rtf.Append("\\par\n");
            WriteBlock(rtf, doc.Blocks[i]);
        }

        rtf.Append('}');
        return rtf.ToString();
    }

    private static void WriteBlock(StringBuilder rtf, BuoyBlock block)
    {
        rtf.Append(@"\pard\plain\f0\fs28");
        if (block.Type != BlockType.Paragraph)
        {
            var indent = (int)(block.Level * ListMarkers.IndentWidth * TwipsPerPoint);
            rtf.Append(CultureInfo.InvariantCulture, $@"\li{indent}\fi0");
        }
        rtf.Append(' ');

        AppendText(rtf, ListMarkers.StoredPrefix(block));
        foreach (var run in block.Runs)
            WriteRun(rtf, run);
    }

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
