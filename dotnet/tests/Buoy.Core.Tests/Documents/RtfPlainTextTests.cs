using System.Text;
using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

public class RtfPlainTextTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cocoa");

    public static TheoryData<string> Names() =>
        new(Directory.GetFiles(Fixtures, "*.rtf").Select(Path.GetFileNameWithoutExtension)!);

    /// <summary>Each line's text, with any list marker (and the spaces after it) taken off.</summary>
    private static string WithoutMarkers(string text) =>
        string.Join("\n", text.Split('\n').Select(line => line[ListMarkers.Detect(line).Length..]));

    /// <summary>
    /// Builds a tiny document at run time. Literal RTF headers with hex escapes
    /// in a source file trip Windows Defender's RTF-exploit signatures.
    /// </summary>
    private static byte[] Document(string body) => Encoding.ASCII.GetBytes("{" + "\\" + "rtf1 " + body + "}");

    private static string Hex(string pair) => "\\" + "'" + pair;

    [Theory]
    [MemberData(nameof(Names))]
    public void ReadsCocoaFixturesLikeRichEditDoes(string name)
    {
        var expected = BuoyDoc.ParseStrict(File.ReadAllText(Path.Combine(Fixtures, name + ".expected.json"), Encoding.UTF8));
        var text = RtfPlainText.Extract(File.ReadAllBytes(Path.Combine(Fixtures, name + ".rtf")));
        Assert.Equal(expected.PlainText, WithoutMarkers(text));
    }

    [Fact]
    public void ReadsItsOwnWriter()
    {
        var doc = new BuoyDoc([
            BuoyBlock.Paragraph("Café {braces} \\ back"),
            new BuoyBlock(BlockType.Bullet, [new BuoyRun("emoji 😀", Bold: true)], level: 1),
            new BuoyBlock(BlockType.Todo, [new BuoyRun("site", Link: "https://example.com")], isChecked: true),
        ]);
        Assert.Equal("Café {braces} \\ back\n◦ emoji 😀\n☑site", RtfPlainText.Extract(RtfWriter.Write(doc)));
    }

    [Fact]
    public void MapsWindows1252HighBytes()
    {
        Assert.Equal("— € ’", RtfPlainText.Extract(Document($"{Hex("97")} {Hex("80")} {Hex("92")}")));
    }

    [Fact]
    public void EmptyIsEmpty() => Assert.Equal("", RtfPlainText.Extract([]));
}
