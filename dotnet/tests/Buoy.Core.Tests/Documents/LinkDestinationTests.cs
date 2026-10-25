using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

public class LinkDestinationTests
{
    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("  example.com/path?q=1 ", "https://example.com/path?q=1")]
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("example.com:8080/x", "https://example.com:8080/x")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("mailto:a@b.c", "mailto:a@b.c")]
    [InlineData("obsidian://open?vault=x", "obsidian://open?vault=x")]
    public void Normalizes(string input, string expected)
    {
        Assert.Equal(expected, LinkDestination.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x")]
    public void RejectsUnusableAddresses(string? input)
    {
        Assert.Null(LinkDestination.Normalize(input));
    }
}
