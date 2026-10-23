using System.Text;
using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

public class RtfWriterTests
{
    private const string Header = "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fnil Segoe UI;}}\n";

    [Fact]
    public void EmptyDocumentIsAnEmptyBlob()
    {
        Assert.Empty(RtfWriter.Write(BuoyDoc.Empty));
    }

    [Fact]
    public void ParagraphsAreSeparatedNotTerminated()
    {
        var rtf = RtfWriter.WriteString(new BuoyDoc([BuoyBlock.Paragraph("a"), BuoyBlock.Paragraph("b")]));
        Assert.Equal(Header + "\\pard\\plain\\f0\\fs28 a\\par\n\\pard\\plain\\f0\\fs28 b}", rtf);
    }

    [Fact]
    public void ListLevelIsTheFirstLineIndent()
    {
        var rtf = RtfWriter.WriteString(new BuoyDoc([
            new BuoyBlock(BlockType.Bullet, [new BuoyRun("a")]),
            new BuoyBlock(BlockType.Bullet, [new BuoyRun("b")], level: 1),
            new BuoyBlock(BlockType.Todo, [new BuoyRun("c")], level: 2, isChecked: true),
        ]));
        Assert.Contains("\\li0\\fi0 \\u8226? a", rtf);
        Assert.Contains("\\li400\\fi0 \\u9702? b", rtf);
        // To-dos are stored without a space after the marker, as the Mac writes them.
        Assert.Contains("\\li800\\fi0 \\u9745?c", rtf);
    }

    [Fact]
    public void FormattingIsGroupedPerRun()
    {
        var rtf = RtfWriter.WriteString(new BuoyDoc([new BuoyBlock(BlockType.Paragraph, [
            new BuoyRun("plain "),
            new BuoyRun("all", Bold: true, Italic: true, Underline: true, Strikethrough: true),
        ])]));
        Assert.Contains("plain {\\b\\i\\ul\\strike all}", rtf);
    }

    [Fact]
    public void LinksAreHyperlinkFields()
    {
        var rtf = RtfWriter.WriteString(new BuoyDoc([new BuoyBlock(BlockType.Paragraph, [
            new BuoyRun("site", Link: "https://example.com/?q=\"x\""),
            new BuoyRun("bold", Bold: true, Link: "https://b.example"),
        ])]));
        Assert.Contains("{\\field{\\*\\fldinst{HYPERLINK \"https://example.com/?q=%22x%22\"}}{\\fldrslt{site}}}", rtf);
        Assert.Contains("{\\fldrslt{\\b bold}}}", rtf);
    }

    [Fact]
    public void SpecialCharactersAreEscaped()
    {
        var rtf = RtfWriter.WriteString(new BuoyDoc([BuoyBlock.Paragraph("a\\b{c}\td\u0001é😀")]));
        Assert.Contains("a\\\\b\\{c\\}\\tab d\\u233?\\u-10179?\\u-8704?}", rtf);
    }

    [Fact]
    public void OutputIsPureAscii()
    {
        var bytes = RtfWriter.Write(new BuoyDoc([BuoyBlock.Paragraph("日本語 ☐ • ◦ ü")]));
        Assert.All(bytes, b => Assert.InRange(b, (byte)0x0A, (byte)0x7E));
        Assert.Equal(Encoding.ASCII.GetString(bytes), RtfWriter.WriteString(new BuoyDoc([BuoyBlock.Paragraph("日本語 ☐ • ◦ ü")])));
    }
}
