using Buoy.Core.Harbor;
using Microsoft.Extensions.Time.Testing;

namespace Buoy.Core.Tests.Harbor;

public class HarborDurationTests
{
    [Theory]
    [InlineData("5m", 300)]
    [InlineData("5 min", 300)]
    [InlineData("5 Mins", 300)]
    [InlineData("5 minutes", 300)]
    [InlineData("90s", 90)]
    [InlineData("2 hrs", 7200)]
    [InlineData("1.5 hours", 5400)]
    [InlineData("1,5 h", 5400)]
    [InlineData("5-minute", 300)]
    [InlineData("1h30m", 5400)]
    [InlineData("30m 1h", 5400)]
    [InlineData("1 hr 30 min", 5400)]
    [InlineData("1 hour and 30 minutes", 5400)]
    [InlineData("2m 30s", 150)]
    [InlineData("1h30", 5400)]
    [InlineData("2m30", 150)]
    [InlineData("a minute", 60)]
    [InlineData("an hour", 3600)]
    [InlineData("five minutes", 300)]
    [InlineData("forty-five seconds", 45)]
    [InlineData("forty five seconds", 45)]
    [InlineData("half an hour", 1800)]
    [InlineData("a quarter hour", 900)]
    [InlineData("1:30", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData("timer 5m", 300)]
    [InlineData("Timer: 5m", 300)]
    [InlineData("5m timer", 300)]
    [InlineData("  10 min!  ", 600)]
    [InlineData("99:59:59", 359_999)]
    public void ReadsDurations(string title, double seconds)
    {
        Assert.Equal(seconds, HarborDuration.Parse(title));
    }

    [Theory]
    [InlineData("Notes from the 5 min standup")]
    [InlineData("Groceries")]
    [InlineData("")]
    [InlineData("timer")]
    [InlineData("5")]
    [InlineData("5m 5m")]
    [InlineData("1h 30s 2")]
    [InlineData("1:3")]
    [InlineData("1:60")]
    [InlineData("1234:00")]
    [InlineData("0s")]
    [InlineData("100 hours")]
    [InlineData("5 lightyears")]
    [InlineData("5m; rm -rf")]
    public void RejectsWhatIsNotWhollyADuration(string title)
    {
        Assert.Null(HarborDuration.Parse(title));
    }
}

public class HarborCountdownTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void CountsDownAndFinishesOnce()
    {
        using var countdown = new HarborCountdown(_clock);
        var finished = 0;
        countdown.Finished += (_, _) => finished++;

        Assert.True(countdown.Start("2s"));
        Assert.Equal("0:02", countdown.DisplayText);
        _clock.Advance(TimeSpan.FromSeconds(1.2));
        Assert.Equal("0:01", countdown.DisplayText);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(countdown.IsFinished);
        Assert.Equal("Time’s up", countdown.DisplayText);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, finished);
    }

    [Fact]
    public void ShowsHoursWhenThereAreAny()
    {
        using var countdown = new HarborCountdown(_clock);
        countdown.Start("1h2m3s");
        Assert.Equal("1:02:03", countdown.DisplayText);
    }

    [Fact]
    public void PausingHoldsTheTime()
    {
        using var countdown = new HarborCountdown(_clock);
        countdown.Start("10s");
        _clock.Advance(TimeSpan.FromSeconds(3.2));
        countdown.TogglePause();
        Assert.True(countdown.IsPaused);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("0:07", countdown.DisplayText);

        countdown.TogglePause();
        _clock.Advance(TimeSpan.FromSeconds(7));
        Assert.True(countdown.IsFinished);
    }

    [Fact]
    public void StartIgnoresOrdinaryTitlesAndARunningTimer()
    {
        using var countdown = new HarborCountdown(_clock);
        Assert.False(countdown.Start("Groceries"));
        Assert.False(countdown.IsActive);
        Assert.True(countdown.Start("5m"));
        Assert.False(countdown.Start("1m")); // already running
        Assert.Equal("5:00", countdown.DisplayText);
    }

    [Fact]
    public void StopClearsEverything()
    {
        using var countdown = new HarborCountdown(_clock);
        countdown.Start("5m");
        countdown.Stop();
        Assert.False(countdown.IsActive);
        Assert.False(countdown.IsFinished);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(countdown.IsFinished);
    }
}
