using System.Text;
using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

/// <summary>
/// Checks on the Cocoa fixtures that don't need RichEdit. Reading them is
/// covered by <c>Buoy.exe --rtf-check</c> (<c>script/rtf-check.ps1</c>).
/// </summary>
public class FixtureTests
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cocoa");

    public static TheoryData<string> Names() =>
        new(System.IO.Directory.GetFiles(Directory, "*.rtf").Select(Path.GetFileNameWithoutExtension)!);

    [Theory]
    [MemberData(nameof(Names))]
    public void FixtureIsAsciiAndItsExpectationIsAStrictBuoyDoc(string name)
    {
        var rtf = File.ReadAllBytes(Path.Combine(Directory, name + ".rtf"));
        Assert.All(rtf, b => Assert.True(b < 0x80, $"{name}.rtf has a non-ASCII byte; Cocoa escapes everything"));

        var expected = BuoyDoc.ParseStrict(File.ReadAllText(Path.Combine(Directory, name + ".expected.json"), Encoding.UTF8));
        Assert.All(RtfWriter.Write(expected), b => Assert.True(b < 0x80));
    }
}
