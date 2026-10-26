using System.Globalization;

namespace Buoy.Core.Harbor;

/// <summary>
/// Reads a note title as a countdown length, a port of the Mac's
/// <c>HarborTimer.duration(in:)</c>. The whole title has to be a duration, so a
/// title that merely mentions one ("Notes from the 5 min standup") stays a note.
///
/// Accepts, case-insensitively and with an optional "timer" before or after:
/// a number and a unit spelled any usual way (<c>5m</c>, <c>5 min</c>,
/// <c>90s</c>, <c>2 hrs</c>, <c>1.5 hours</c>, <c>1,5 h</c>, <c>5-minute</c>);
/// compounds in any order, each unit once (<c>1h30m</c>, <c>1 hour and 30
/// minutes</c>); a trailing bare number meaning the next smaller unit
/// (<c>1h30</c>); words (<c>a minute</c>, <c>forty-five seconds</c>,
/// <c>half an hour</c>, <c>a quarter hour</c>); and clock form (<c>1:30</c>,
/// <c>1:02:03</c>). Between 1 second and 99:59:59.
/// </summary>
public static class HarborDuration
{
    private static readonly Dictionary<string, double> UnitSeconds = new()
    {
        ["s"] = 1, ["sec"] = 1, ["secs"] = 1, ["second"] = 1, ["seconds"] = 1,
        ["m"] = 60, ["min"] = 60, ["mins"] = 60, ["minute"] = 60, ["minutes"] = 60,
        ["h"] = 3600, ["hr"] = 3600, ["hrs"] = 3600, ["hour"] = 3600, ["hours"] = 3600,
    };

    private static readonly Dictionary<string, double> NumberWords = new()
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11,
        ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
        ["ninety"] = 90,
    };

    private static readonly (string Phrase, string Replacement)[] Phrases =
    [
        ("half an hour", "30 min"), ("half a hour", "30 min"), ("half hour", "30 min"),
        ("half a minute", "30 sec"), ("half minute", "30 sec"),
        ("a quarter of an hour", "15 min"), ("quarter of an hour", "15 min"),
        ("a quarter hour", "15 min"), ("quarter hour", "15 min"),
    ];

    /// <summary>The countdown length in seconds, or null if the title isn't wholly a duration.</summary>
    public static double? Parse(string title)
    {
        var text = title.ToLowerInvariant().Trim();
        while (text.Length > 0 && text[^1] is '.' or '!') text = text[..^1];
        if (text.StartsWith("timer", StringComparison.Ordinal))
        {
            text = text["timer".Length..];
            if (text.StartsWith(':')) text = text[1..];
        }
        else if (text.EndsWith("timer", StringComparison.Ordinal))
        {
            text = text[..^"timer".Length];
        }
        text = text.Trim();
        if (text.Length == 0) return null;

        var seconds = Clock(text) ?? Units(text);
        // Bounded, and short enough to stay readable in the pill.
        if (seconds is not { } value || !double.IsFinite(value) || value < 1 || value > 359_999) return null;
        return value;
    }

    /// <summary><c>m:ss</c> or <c>h:mm:ss</c>.</summary>
    private static double? Clock(string text)
    {
        var parts = text.Split(':');
        if (parts.Length is not (2 or 3)) return null;
        if (parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;
        if (parts.Skip(1).Any(p => p.Length != 2) || parts[0].Length > 3) return null;
        var values = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToList();
        if (values.Skip(1).Any(v => v >= 60)) return null;
        return values.Aggregate(0.0, (total, next) => total * 60 + next);
    }

    /// <summary>One or more number-and-unit pairs.</summary>
    private static double? Units(string text)
    {
        var normalized = " " + text + " ";
        foreach (var (phrase, replacement) in Phrases)
            normalized = normalized.Replace(phrase, replacement, StringComparison.Ordinal);
        normalized = normalized.Replace(" and ", " ").Replace('-', ' ').Replace(", ", " ");

        // Runs of digits (with a decimal separator) and runs of letters.
        var tokens = new List<string>();
        var current = "";
        var currentIsNumber = false;
        void Flush()
        {
            if (current.Length > 0) tokens.Add(current);
            current = "";
        }
        foreach (var c in normalized)
        {
            if (char.IsAsciiDigit(c) || (c is '.' or ',' && currentIsNumber))
            {
                if (!currentIsNumber) Flush();
                currentIsNumber = true;
                current += c;
            }
            else if (char.IsLetter(c))
            {
                if (currentIsNumber) Flush();
                currentIsNumber = false;
                current += c;
            }
            else if (c == ' ')
            {
                Flush();
            }
            else
            {
                return null;
            }
        }
        Flush();

        var components = new List<(double Amount, double? Unit)>();
        var index = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            double amount;
            if (char.IsAsciiDigit(token[0]))
            {
                if (!double.TryParse(token.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
                    return null;
            }
            else if (NumberWords.TryGetValue(token, out var word))
            {
                amount = word;
                // "forty five" is 45.
                if (word >= 20 && word % 10 == 0 && index + 1 < tokens.Count
                    && tokens[index + 1] is not ("a" or "an")
                    && NumberWords.TryGetValue(tokens[index + 1], out var ones) && ones < 10)
                {
                    amount += ones;
                    index++;
                }
            }
            else
            {
                return null;
            }
            index++;

            double? unit = null;
            if (index < tokens.Count && UnitSeconds.TryGetValue(tokens[index], out var seconds))
            {
                unit = seconds;
                index++;
            }
            components.Add((amount, unit));
        }
        if (components.Count == 0) return null;

        var usedUnits = new HashSet<double>();
        var total = 0.0;
        for (var position = 0; position < components.Count; position++)
        {
            var (amount, explicitUnit) = components[position];
            double unit;
            if (explicitUnit is { } given)
            {
                unit = given;
            }
            else
            {
                // Only a final bare number may omit its unit, and only after one
                // with a smaller unit to fall to: "1h30", "2m30".
                if (position != components.Count - 1 || position == 0
                    || components[position - 1].Unit is not { } previous || previous <= 1)
                    return null;
                unit = previous / 60;
            }
            if (!usedUnits.Add(unit)) return null;
            total += amount * unit;
        }
        return total;
    }
}
