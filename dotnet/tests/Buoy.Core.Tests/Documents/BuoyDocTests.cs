using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

public class BuoyDocTests
{
    private static readonly BuoyDoc Sample = new([
        new BuoyBlock(BlockType.Paragraph, [new BuoyRun("Groceries "), new BuoyRun("today", Bold: true)]),
        new BuoyBlock(BlockType.Bullet, [new BuoyRun("eggs")]),
        new BuoyBlock(BlockType.Todo, [new BuoyRun("milk", Strikethrough: true)], level: 1, isChecked: true),
        new BuoyBlock(BlockType.Paragraph, [new BuoyRun("recipe", Link: "https://example.com")]),
    ]);

    [Fact]
    public void JsonRoundTripsBothWays()
    {
        var json = Sample.ToJson();
        Assert.Equal(Sample, BuoyDoc.ParseStrict(json));
        Assert.Equal(Sample, BuoyDoc.ParseLenient(json));
    }

    [Fact]
    public void JsonOmitsDefaults()
    {
        Assert.Equal(
            """{"v":1,"blocks":[{"type":"paragraph","runs":[{"text":"hi"}]},{"type":"todo","level":0,"checked":false,"runs":[{"text":"x","i":true}]}]}""",
            new BuoyDoc([BuoyBlock.Paragraph("hi"), new BuoyBlock(BlockType.Todo, [new BuoyRun("x", Italic: true)])]).ToJson());
    }

    [Fact]
    public void RunsAreCanonicalized()
    {
        var block = new BuoyBlock(BlockType.Paragraph, [
            new BuoyRun("a"), new BuoyRun(""), new BuoyRun("b"), new BuoyRun("c", Bold: true), new BuoyRun("d", Bold: true),
        ]);
        Assert.Equal([new BuoyRun("ab"), new BuoyRun("cd", Bold: true)], block.Runs);
    }

    [Fact]
    public void SingleEmptyParagraphIsTheEmptyDocument()
    {
        Assert.Equal(BuoyDoc.Empty, new BuoyDoc([BuoyBlock.Paragraph("")]));
        // A trailing empty line after text is kept.
        Assert.Equal(2, new BuoyDoc([BuoyBlock.Paragraph("a"), BuoyBlock.Paragraph("")]).Blocks.Count);
    }

    [Fact]
    public void ParagraphsIgnoreLevelAndChecked()
    {
        var block = new BuoyBlock(BlockType.Paragraph, [new BuoyRun("x")], level: 2, isChecked: true);
        Assert.Equal(0, block.Level);
        Assert.False(block.Checked);
    }

    [Fact]
    public void PlainTextJoinsBlocksWithoutMarkers()
    {
        Assert.Equal("Groceries today\neggs\nmilk\nrecipe", Sample.PlainText);
    }

    [Fact]
    public void CopyTextKeepsListMarkers()
    {
        Assert.Equal("Groceries today\n• eggs\n☑ milk\nrecipe", Sample.CopyText);
    }

    [Theory]
    [InlineData("""{"v":2,"blocks":[]}""")]
    [InlineData("""{"blocks":[]}""")]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"heading","runs":[]}]}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"bullet","level":3,"runs":[]}]}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"bullet","level":-1,"runs":[]}]}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"paragraph","runs":[{"text":"a\nb"}]}]}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"paragraph","runs":[{"text":"a","link":"javascript:alert(1)"}]}]}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"paragraph","runs":[{"text":"a","link":"not a url"}]}]}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"paragraph"}]}""")]
    [InlineData("""{"v":1,"blocks":[{"type":"paragraph","runs":[{"b":true}]}]}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void StrictRejectsAnythingOutsideTheFormat(string json)
    {
        Assert.Throws<BuoyDocFormatException>(() => BuoyDoc.ParseStrict(json));
    }

    [Fact]
    public void LenientKeepsTheText()
    {
        var doc = BuoyDoc.ParseLenient("""
            {"v":2,"extra":1,"blocks":[
              {"type":"heading","runs":[{"text":"Title","color":"red"}]},
              {"type":"bullet","level":7,"runs":[{"text":"a\nb","link":"javascript:x"}]}
            ]}
            """);
        Assert.Equal(new BuoyDoc([
            BuoyBlock.Paragraph("Title"),
            new BuoyBlock(BlockType.Bullet, [new BuoyRun("a b")], level: 2),
        ]), doc);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("obsidian://open?vault=x", true)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("file:///C:/Windows", false)]
    [InlineData("/relative", false)]
    [InlineData(null, false)]
    public void LinkPolicy(string? link, bool allowed)
    {
        Assert.Equal(allowed, BuoyDoc.IsAllowedLink(link));
    }
}
