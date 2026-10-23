using Buoy.Core.Settings;

namespace Buoy.Core.Tests.Settings;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+N", ChordModifiers.Ctrl | ChordModifiers.Alt, 'N')]
    [InlineData("ctrl + shift + space", ChordModifiers.Ctrl | ChordModifiers.Shift, 0x20)]
    [InlineData("Win+Alt+7", ChordModifiers.Win | ChordModifiers.Alt, '7')]
    [InlineData("Control+F12", ChordModifiers.Ctrl, 0x7B)]
    [InlineData("Alt+n", ChordModifiers.Alt, 'N')]
    public void Parses(string text, ChordModifiers modifiers, int key)
    {
        Assert.Equal(new KeyChord(modifiers, key), KeyChord.Parse(text));
    }

    [Theory]
    [InlineData("Option+Cmd+N")]   // a Mac settings file
    [InlineData("N")]
    [InlineData("Shift+N")]
    [InlineData("Ctrl+Ctrl+N")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+F25")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsWhatItCannotRegister(string? text)
    {
        Assert.Null(KeyChord.Parse(text));
    }

    [Theory]
    [InlineData("Ctrl+Alt+N")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Alt+Win+F5")]
    public void PrintsCanonically(string text)
    {
        Assert.Equal(text, KeyChord.Parse(text)!.ToString());
    }

    [Fact]
    public void DefaultIsCtrlAltN()
    {
        Assert.Equal("Ctrl+Alt+N", KeyChord.Default.ToString());
    }
}
