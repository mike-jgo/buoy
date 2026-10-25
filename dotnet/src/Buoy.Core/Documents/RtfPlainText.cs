using System.Text;

namespace Buoy.Core.Documents;

/// <summary>
/// The text of a stored note's RTF, without RichEdit: for search, which runs
/// over every note, including ones from a Mac that have no BuoyDoc stored yet.
/// Handles what Cocoa and <see cref="RtfWriter"/> write: <c>\par</c> and
/// <c>\</c>-newline breaks, <c>\uN</c> with <c>\ucN</c> fallbacks, <c>\'xx</c>
/// (Windows-1252), escapes, and skipped destinations (font and colour tables,
/// <c>{\*…}</c>, link instructions). List markers stay in, as stored.
/// </summary>
public static class RtfPlainText
{
    private static readonly HashSet<string> SkippedDestinations =
    [
        "fonttbl", "colortbl", "expandedcolortbl", "stylesheet", "info", "pict", "fldinst",
        "header", "footer", "listtable", "listoverridetable", "themedata", "colorschememapping",
    ];

    // Windows-1252 differs from Latin-1 only in 0x80–0x9F.
    private static readonly char[] Cp1252High =
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ".ToCharArray();

    public static string Extract(byte[] rtf)
    {
        if (rtf.Length == 0) return "";
        var text = Encoding.Latin1.GetString(rtf);
        var output = new StringBuilder();
        var skipStack = new Stack<(bool Skip, int UnicodeSkip)>();
        var skip = false;
        var unicodeSkip = 1;
        var pendingFallback = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '{':
                    skipStack.Push((skip, unicodeSkip));
                    pendingFallback = 0;
                    continue;
                case '}':
                    if (skipStack.Count > 0) (skip, unicodeSkip) = skipStack.Pop();
                    pendingFallback = 0;
                    continue;
                case '\r' or '\n':
                    continue;
                case '\\':
                    break;
                default:
                    if (pendingFallback > 0) { pendingFallback--; continue; }
                    if (!skip) output.Append(c);
                    continue;
            }

            // A backslash: control symbol or control word.
            if (i + 1 >= text.Length) break;
            var next = text[i + 1];
            if (!char.IsAsciiLetter(next))
            {
                i++;
                if (pendingFallback > 0 && next != '\'') { pendingFallback--; continue; }
                switch (next)
                {
                    case '\\' or '{' or '}': if (!skip) output.Append(next); break;
                    case '\r' or '\n': if (!skip) output.Append('\n'); break; // Cocoa's paragraph break
                    case '~': if (!skip) output.Append(' '); break;
                    case '_': if (!skip) output.Append('-'); break;
                    case '*': skip = true; break;
                    case '\'':
                        if (i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                        {
                            i += 2;
                            if (pendingFallback > 0) { pendingFallback--; break; }
                            if (!skip) output.Append(b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b);
                        }
                        break;
                }
                continue;
            }

            var start = i + 1;
            var end = start;
            while (end < text.Length && char.IsAsciiLetter(text[end])) end++;
            var word = text[start..end];
            var parameterStart = end;
            if (end < text.Length && text[end] == '-') end++;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            int? parameter = end > parameterStart && int.TryParse(text.AsSpan(parameterStart, end - parameterStart), out var p) ? p : null;
            if (end < text.Length && text[end] == ' ') end++; // the delimiting space belongs to the word
            i = end - 1;

            switch (word)
            {
                case "par" or "line":
                    if (!skip) output.Append('\n');
                    break;
                case "tab":
                    if (!skip) output.Append('\t');
                    break;
                case "uc":
                    unicodeSkip = parameter ?? 1;
                    break;
                case "u" when parameter is { } code:
                    if (!skip) output.Append((char)(ushort)(short)code);
                    pendingFallback = unicodeSkip;
                    break;
                default:
                    if (SkippedDestinations.Contains(word)) skip = true;
                    break;
            }
        }

        return output.ToString();
    }
}
