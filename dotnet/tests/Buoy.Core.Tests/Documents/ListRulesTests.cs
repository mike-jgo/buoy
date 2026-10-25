using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

public class ListRulesTests
{
    [Theory]
    [InlineData("-", BlockType.Bullet)]
    [InlineData("[]", BlockType.Todo)]
    [InlineData("- ", null)]
    [InlineData("a-", null)]
    [InlineData("[ ]", null)]
    [InlineData("", null)]
    public void AutoComplete(string before, BlockType? expected)
    {
        Assert.Equal(expected, ListRules.AutoComplete(before));
    }

    [Fact]
    public void ReturnContinuesABulletWithItsOwnMarker()
    {
        Assert.Equal(new ReturnOutcome.Continue("• ", 0), ListRules.OnReturn("• eggs", 0));
        Assert.Equal(new ReturnOutcome.Continue("◦ ", 1), ListRules.OnReturn("◦ socks", 1));
    }

    [Fact]
    public void ReturnContinuesATodoUnchecked()
    {
        Assert.Equal(new ReturnOutcome.Continue("☐ ", 1), ListRules.OnReturn("☑ done", 1));
    }

    [Theory]
    [InlineData("• ", 0, 2, false)]
    [InlineData("•   ", 0, 2, false)]
    [InlineData("◦ ", 1, 2, true)]
    [InlineData("☐ ", 0, 2, false)]
    [InlineData("☐ ", 2, 2, true)]
    [InlineData("☐", 1, 1, true)]
    public void ReturnOnAnEmptyItemEndsTheList(string before, int level, int markerLength, bool reset)
    {
        Assert.Equal(new ReturnOutcome.EndList(markerLength, reset), ListRules.OnReturn(before, level));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("•no space")]
    public void ReturnOnOtherLinesIsAPlainBreak(string before)
    {
        Assert.Equal(new ReturnOutcome.NotList(), ListRules.OnReturn(before, 0));
    }

    [Theory]
    [InlineData("• ", false)]
    [InlineData("◦ ", true)]
    [InlineData("☐ ", true)]
    [InlineData("☑ ", true)]
    public void BackspaceRightAfterAMarkerRemovesIt(string before, bool reset)
    {
        Assert.Equal(new BackspaceOutcome.RemoveMarker(2, reset), ListRules.OnBackspace(before));
    }

    [Theory]
    [InlineData("• a")]
    [InlineData("•")]
    [InlineData("text ")]
    [InlineData("")]
    public void BackspaceElsewhereIsOrdinary(string before)
    {
        Assert.Equal(new BackspaceOutcome.NotList(), ListRules.OnBackspace(before));
    }

    [Fact]
    public void PastedDashAndStarLinesBecomeBullets()
    {
        var promoted = ListRules.PromotePlainBullets([
            new BuoyBlock(BlockType.Paragraph, [new BuoyRun("  - "), new BuoyRun("eggs", Bold: true)]),
            BuoyBlock.Paragraph("* milk"),
            BuoyBlock.Paragraph("-not a list"),
            BuoyBlock.Paragraph("plain"),
            new BuoyBlock(BlockType.Todo, [new BuoyRun("- stays a to-do")]),
        ]).ToList();

        Assert.Equal(new BuoyBlock(BlockType.Bullet, [new BuoyRun("eggs", Bold: true)]), promoted[0]);
        Assert.Equal(new BuoyBlock(BlockType.Bullet, [new BuoyRun("milk")]), promoted[1]);
        Assert.Equal(BuoyBlock.Paragraph("-not a list"), promoted[2]);
        Assert.Equal(BuoyBlock.Paragraph("plain"), promoted[3]);
        Assert.Equal(BlockType.Todo, promoted[4].Type);
    }

    [Theory]
    [InlineData(BlockType.Bullet, 0, false, 1)]
    [InlineData(BlockType.Todo, 1, false, 2)]
    [InlineData(BlockType.Bullet, 2, false, null)]
    [InlineData(BlockType.Todo, 2, true, 1)]
    [InlineData(BlockType.Bullet, 0, true, null)]
    [InlineData(BlockType.Paragraph, 0, false, null)]
    public void TabChangesTheLevelWithinLimits(BlockType type, int level, bool shift, int? expected)
    {
        Assert.Equal(expected, ListRules.OnTab(type, level, shift));
    }
}
