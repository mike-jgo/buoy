using System.Text.Json.Nodes;
using Buoy.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Buoy.Core.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void MissingKeysKeepTheirDefaults()
    {
        var settings = AppSettings.Parse("""{"fontSize": 16}""");
        Assert.Equal(16, settings.FontSize);
        Assert.True(settings.AlwaysOnTop);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(AppSettings.DefaultGlobalShortcut, settings.GlobalShortcut);
    }

    [Fact]
    public void MalformedValuesFallBackOneKeyAtATime()
    {
        var settings = AppSettings.Parse("""{"alwaysOnTop": "yes", "fontSize": 18, "theme": 3, "lastSelectedNoteID": 5}""");
        Assert.True(settings.AlwaysOnTop);
        Assert.Equal(18, settings.FontSize);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Null(settings.LastSelectedNoteId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void UnreadableFilesGiveDefaults(string json)
    {
        Assert.Equal(new AppSettings().ToJson(), AppSettings.Parse(json).ToJson());
    }

    [Fact]
    public void UnknownKeysSurviveAWrite()
    {
        var settings = AppSettings.Parse("""{"theme": "dark", "harborFlavour": {"x": 1}, "lastSelectedNoteID": "42"}""");
        settings.FontSize = 20;
        settings.LastSelectedNoteId = null;

        var written = JsonNode.Parse(settings.ToJson())!.AsObject();
        Assert.Equal("dark", (string?)written["theme"]);
        Assert.Equal(20, (double?)written["fontSize"]);
        Assert.Equal(1, (int?)written["harborFlavour"]!["x"]);
        Assert.False(written.ContainsKey("lastSelectedNoteID"), "a cleared value is removed, not left stale");
    }

    [Fact]
    public void RoundTrips()
    {
        var settings = new AppSettings
        {
            AlwaysOnTop = false, LaunchAtLogin = true, FontSize = 12.5, Theme = AppTheme.Light,
            GlobalShortcut = "Ctrl+Shift+Space", LastSelectedNoteId = "1", SyncServerUrl = "https://buoy.example.ts.net",
            SyncTokenProtected = "AQAAANCMnd8B",
        };
        Assert.Equal(settings.ToJson(), AppSettings.Parse(settings.ToJson()).ToJson());
    }

    [Fact]
    public void TheStoreWritesOnceAfterTheDelay()
    {
        using var dir = new TempDirectory();
        var clock = new FakeTimeProvider();
        using var store = new SettingsStore(dir.Paths, clock);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Update(s => s.FontSize = 15);
        store.Update(s => s.FontSize = 16);
        Assert.Equal(2, changes);
        Assert.False(File.Exists(dir.Paths.Settings));

        clock.Advance(SettingsStore.WriteDelay);
        Assert.Equal(16, AppSettings.Parse(File.ReadAllText(dir.Paths.Settings)).FontSize);
        Assert.False(File.Exists(dir.Paths.Settings + ".tmp"));

        store.Update(s => s.Theme = AppTheme.Dark);
        store.Flush();
        Assert.Equal(AppTheme.Dark, new SettingsStore(dir.Paths, clock).Value.Theme);
    }
}
