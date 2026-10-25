using Buoy.Core.Documents;

namespace Buoy.Core.Tests.Documents;

public class TimestampTextTests
{
    private static readonly TimeZoneInfo Manila = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 17, 0, 0, TimeSpan.FromHours(8));

    private static long At(int day, int hour, int minute) =>
        new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();

    [Fact]
    public void TodayIsJustTheTime() => Assert.Equal("2:34 PM", TimestampText.Format(At(23, 14, 34), Now, Manila));

    [Fact]
    public void YesterdaySaysSo() => Assert.Equal("Yesterday 9:05 AM", TimestampText.Format(At(22, 9, 5), Now, Manila));

    [Fact]
    public void OlderShowsTheDate() => Assert.Equal("Oct 18 11:00 PM", TimestampText.Format(At(18, 23, 0), Now, Manila));

    [Fact]
    public void DaysFollowTheLocalZoneNotUtc()
    {
        // 00:30 local on the 23rd is still the 22nd in UTC.
        Assert.Equal("12:30 AM", TimestampText.Format(At(23, 0, 30), Now, Manila));
    }
}
