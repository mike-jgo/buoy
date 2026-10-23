using System.Text;
using Buoy.Core.Documents;
using Buoy.Windows.Editor;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Buoy.Windows.Dev;

/// <summary>
/// <c>Buoy.exe --rtf-check &lt;fixtures&gt; &lt;report&gt;</c>: runs every
/// <c>*.rtf</c> fixture through a real RichEditBox and writes a report.
///
/// RichEdit can't be loaded from a unit test, so this is the regression check
/// for <see cref="RichEditConversion"/>. For each fixture it checks three trips:
/// Cocoa RTF → BuoyDoc against the expected JSON, BuoyDoc → <see cref="RtfWriter"/>
/// → BuoyDoc, and the expected JSON → RtfWriter → BuoyDoc.
/// </summary>
internal static class RtfCheck
{
    public static void Start(string fixturesDirectory, string reportPath)
    {
        var box = new RichEditBox();
        var window = new Window { Content = box, Title = "Buoy RTF check" };
        box.Loaded += (_, _) =>
        {
            string report;
            try { report = Run(box.Document, fixturesDirectory); }
            catch (Exception e) { report = $"FAIL crashed: {e}\n"; }
            File.WriteAllText(reportPath, report, Encoding.UTF8);
            Application.Current.Exit();
        };
        window.Activate();
    }

    private static string Run(RichEditTextDocument document, string fixturesDirectory)
    {
        var report = new StringBuilder();
        var failures = 0;
        foreach (var rtfPath in Directory.GetFiles(fixturesDirectory, "*.rtf").Order())
        {
            var name = Path.GetFileNameWithoutExtension(rtfPath);
            var expected = BuoyDoc.ParseStrict(File.ReadAllText(Path.ChangeExtension(rtfPath, ".expected.json")));

            RichEditConversion.LoadRtf(document, File.ReadAllBytes(rtfPath));
            var dump = Dump(document);
            var fromCocoa = RichEditConversion.Read(document);

            RichEditConversion.LoadRtf(document, RtfWriter.Write(fromCocoa));
            var roundTripped = RichEditConversion.Read(document);

            var expectedRtf = RtfWriter.Write(expected);
            RichEditConversion.LoadRtf(document, expectedRtf);
            var fromWriter = RichEditConversion.Read(document);

            failures += Check(report, name, "cocoa → doc", expected, fromCocoa);
            failures += Check(report, name, "doc → rtf → doc", fromCocoa, roundTripped);
            failures += Check(report, name, "expected → rtf → doc", expected, fromWriter);

            report.AppendLine($"  -- RichEdit view of {name}.rtf");
            report.Append(dump);
            report.AppendLine($"  -- RtfWriter output for expected");
            report.AppendLine("  " + Encoding.ASCII.GetString(expectedRtf).Replace("\n", "\n  "));
        }
        report.Insert(0, failures == 0 ? "RESULT PASS\n" : $"RESULT FAIL ({failures})\n");
        return report.ToString();
    }

    private static int Check(StringBuilder report, string name, string trip, BuoyDoc expected, BuoyDoc actual)
    {
        if (expected.Equals(actual))
        {
            report.AppendLine($"PASS {name}: {trip}");
            return 0;
        }
        report.AppendLine($"FAIL {name}: {trip}");
        report.AppendLine($"  expected {expected.ToJson()}");
        report.AppendLine($"  actual   {actual.ToJson()}");
        return 1;
    }

    /// <summary>What RichEdit made of the document: per paragraph indents and per run formatting.</summary>
    private static string Dump(RichEditTextDocument document)
    {
        var dump = new StringBuilder();
        var story = document.GetRange(0, 0);
        story.Expand(TextRangeUnit.Story);
        var position = 0;
        while (position < story.EndPosition)
        {
            var paragraph = document.GetRange(position, position);
            paragraph.Expand(TextRangeUnit.Paragraph);
            if (paragraph.EndPosition <= position) break;
            var format = paragraph.ParagraphFormat;
            dump.AppendLine($"  ¶ [{paragraph.StartPosition},{paragraph.EndPosition}) left={format.LeftIndent} first={format.FirstLineIndent}");

            var run = paragraph.StartPosition;
            while (run < paragraph.EndPosition)
            {
                var range = document.GetRange(run, run);
                range.Expand(TextRangeUnit.CharacterFormat);
                var end = Math.Min(range.EndPosition, paragraph.EndPosition);
                if (end <= run) end = run + 1;
                range.SetRange(run, end);
                range.GetText(TextGetOptions.None, out var text);
                var c = range.CharacterFormat;
                dump.AppendLine($"     [{run},{end}) {Escape(text)} b={c.Bold} i={c.Italic} u={c.Underline} s={c.Strikethrough} hidden={c.Hidden} link={range.Link}");
                run = end;
            }
            position = paragraph.EndPosition;
        }
        return dump.ToString();
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder("\"");
        foreach (var ch in text)
            escaped.Append(ch is >= ' ' and <= '~' ? ch.ToString() : $"\\u{(int)ch:X4}");
        return escaped.Append('"').ToString();
    }
}
