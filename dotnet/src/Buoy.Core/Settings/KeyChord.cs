namespace Buoy.Core.Settings;

[Flags]
public enum ChordModifiers
{
    None = 0,
    // The values are Win32's MOD_* flags, so RegisterHotKey can take them as-is.
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// A global shortcut such as <c>Ctrl+Alt+N</c>, as stored in
/// <c>settings.globalShortcut</c>. <see cref="VirtualKey"/> is a Win32 VK code.
/// A chord needs Ctrl, Alt or Win: a bare key or Shift+key would swallow
/// ordinary typing in every app.
/// </summary>
public sealed record KeyChord(ChordModifiers Modifiers, int VirtualKey)
{
    public static KeyChord Default { get; } = Parse(AppSettings.DefaultGlobalShortcut)!;

    /// <summary>Reads a chord, or returns null for anything this can't register (Mac strings like <c>Option+Cmd+N</c> included).</summary>
    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0)) return null;

        var modifiers = ChordModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ChordModifiers.Ctrl,
                "alt" => ChordModifiers.Alt,
                "shift" => ChordModifiers.Shift,
                "win" => ChordModifiers.Win,
                _ => ChordModifiers.None,
            };
            if (modifier == ChordModifiers.None || modifiers.HasFlag(modifier)) return null;
            modifiers |= modifier;
        }
        if ((modifiers & (ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Win)) == 0) return null;

        var key = KeyFromName(parts[^1]);
        return key is null ? null : new KeyChord(modifiers, key.Value);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ChordModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ChordModifiers.Win)) parts.Add("Win");
        parts.Add(NameFromKey(VirtualKey));
        return string.Join("+", parts);
    }

    private static int? KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c; // VK_A… and VK_0… are the ASCII codes
        }
        if (name.Length is 2 or 3 && char.ToUpperInvariant(name[0]) == 'F'
            && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return 0x6F + f; // VK_F1 = 0x70
        return name.ToLowerInvariant() switch
        {
            "space" => 0x20,
            _ => null,
        };
    }

    private static string NameFromKey(int vk) => vk switch
    {
        0x20 => "Space",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => ((char)vk).ToString(),
    };
}
