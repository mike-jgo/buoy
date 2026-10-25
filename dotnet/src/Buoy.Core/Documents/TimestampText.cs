using System.Globalization;

namespace Buoy.Core.Documents;

/// <summary>The footer's times, worded as the Mac's <c>TimestampFormatter</c>.</summary>
public static class TimestampText
{
    /// <summary>
    /// Today: <c>2:34 PM</c>. Yesterday: <c>Yesterday 2:34 PM</c>. Older: <c>Mar 18 2:34 PM</c>.
    /// </summary>
    public static string Format(long unixMilliseconds, DateTimeOffset now, TimeZoneInfo zone)
    {
        var time = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds), zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var clock = time.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (time.Date == today) return clock;
        if (time.Date == today.AddDays(-1)) return $"Yesterday {clock}";
        return time.ToString("MMM d h:mm tt", CultureInfo.InvariantCulture);
    }
}
