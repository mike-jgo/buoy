using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

public class ListMarkersTests
{
    [Theory]
    [InlineData("• eggs", BlockType.Bullet, false, 2)]
    [InlineData("◦ eggs", BlockType.Bullet, false, 2)]
    [InlineData("•eggs", BlockType.Paragraph, false, 0)]
    [InlineData("•", BlockType.Paragraph, false, 0)]
    [InlineData("☐milk", BlockType.Todo, false, 1)]
    [InlineData("☑milk", BlockType.Todo, true, 1)]
    [InlineData("☐ milk", BlockType.Todo, false, 2)]
    [InlineData("☐   milk", BlockType.Todo, false, 4)]
    [InlineData("☐", BlockType.Todo, false, 1)]
    [InlineData("plain", BlockType.Paragraph, false, 0)]
    [InlineData("", BlockType.Paragraph, false, 0)]
    public void Detect(string text, BlockType type, bool isChecked, int length)
    {
        Assert.Equal((type, isChecked, length), ListMarkers.Detect(text));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 1)]
    [InlineData(19.95, 1)]
    [InlineData(40, 2)]
    [InlineData(60, 2)]
    [InlineData(-5, 0)]
    public void LevelFromFirstLineIndent(double points, int level)
    {
        Assert.Equal(level, ListMarkers.LevelFromFirstLineIndent(points));
    }

    [Fact]
    public void StoredAndEditorPrefixesDifferOnlyForTodos()
    {
        var bullet = new BuoyBlock(BlockType.Bullet, [], level: 1);
        var todo = new BuoyBlock(BlockType.Todo, [], isChecked: true);
        Assert.Equal("◦ ", ListMarkers.StoredPrefix(bullet));
        Assert.Equal("◦ ", ListMarkers.EditorPrefix(bullet));
        Assert.Equal("☑", ListMarkers.StoredPrefix(todo));
        Assert.Equal("☑ ", ListMarkers.EditorPrefix(todo));
    }
}
