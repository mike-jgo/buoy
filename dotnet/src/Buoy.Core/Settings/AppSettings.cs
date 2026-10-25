using System.Text.Json;
using System.Text.Json.Nodes;

namespace Buoy.Core.Settings;

public enum AppTheme { System, Light, Dark }

/// <summary>What the footer's small readout shows; clicking it moves to the next.</summary>
public enum FooterInfo { LastEdited, Created, Characters, Words }

/// <summary>
/// <c>settings.json</c>. Shares key names with the Mac's <c>AppSettings</c>
/// where the meaning is the same, reads every key leniently (a missing or
/// malformed key keeps its default, so a new field never resets the file), and
/// keeps keys it doesn't know when it writes the file back.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultGlobalShortcut = "Ctrl+Alt+N";

    public bool AlwaysOnTop { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public double FontSize { get; set; } = 14;
    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Like <c>Ctrl+Alt+N</c>. The app falls back to the default when it can't register it.</summary>
    public string GlobalShortcut { get; set; } = DefaultGlobalShortcut;
    public string? LastSelectedNoteId { get; set; }
    public string? SyncServerUrl { get; set; }
    /// <summary>The sync token, encrypted by the app (DPAPI). Never the plain token.</summary>
    public string? SyncTokenProtected { get; set; }
    /// <summary>Sticks across notes and launches, as on the Mac.</summary>
    public FooterInfo FooterInfo { get; set; } = FooterInfo.LastEdited;

    /// <summary>Everything read from the file, including keys this build doesn't use.</summary>
    private JsonObject _raw = [];

    private const string AlwaysOnTopKey = "alwaysOnTop";
    private const string LaunchAtLoginKey = "launchAtLogin";
    private const string FontSizeKey = "fontSize";
    private const string ThemeKey = "theme";
    private const string GlobalShortcutKey = "globalShortcut";
    private const string LastSelectedNoteIdKey = "lastSelectedNoteID";
    private const string SyncServerUrlKey = "syncServerURL";
    private const string SyncTokenKey = "syncToken";
    private const string FooterInfoKey = "footerInfo";

    public static AppSettings Parse(string json)
    {
        JsonObject raw;
        try { raw = JsonNode.Parse(json) as JsonObject ?? []; }
        catch (JsonException) { raw = []; }

        var settings = new AppSettings { _raw = raw };
        settings.AlwaysOnTop = Read(raw, AlwaysOnTopKey, settings.AlwaysOnTop);
        settings.LaunchAtLogin = Read(raw, LaunchAtLoginKey, settings.LaunchAtLogin);
        settings.FontSize = Read(raw, FontSizeKey, settings.FontSize);
        settings.Theme = Read(raw, ThemeKey, "system") switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };
        settings.GlobalShortcut = Read(raw, GlobalShortcutKey, settings.GlobalShortcut);
        settings.LastSelectedNoteId = Read<string?>(raw, LastSelectedNoteIdKey, null);
        settings.SyncServerUrl = Read<string?>(raw, SyncServerUrlKey, null);
        settings.SyncTokenProtected = Read<string?>(raw, SyncTokenKey, null);
        settings.FooterInfo = Enum.TryParse<FooterInfo>(Read(raw, FooterInfoKey, ""), ignoreCase: true, out var info)
            && Enum.IsDefined(info) ? info : FooterInfo.LastEdited;
        return settings;
    }

    public string ToJson()
    {
        var json = (JsonObject)_raw.DeepClone();
        json[AlwaysOnTopKey] = AlwaysOnTop;
        json[LaunchAtLoginKey] = LaunchAtLogin;
        json[FontSizeKey] = FontSize;
        json[ThemeKey] = Theme.ToString().ToLowerInvariant();
        json[GlobalShortcutKey] = GlobalShortcut;
        Write(json, LastSelectedNoteIdKey, LastSelectedNoteId);
        Write(json, SyncServerUrlKey, SyncServerUrl);
        Write(json, SyncTokenKey, SyncTokenProtected);
        json[FooterInfoKey] = char.ToLowerInvariant(FooterInfo.ToString()[0]) + FooterInfo.ToString()[1..];
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public AppSettings Clone() => Parse(ToJson());

    private static T Read<T>(JsonObject raw, string key, T fallback)
    {
        try { return raw[key] is { } node ? node.GetValue<T>() : fallback; }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return fallback; }
    }

    private static void Write(JsonObject json, string key, string? value)
    {
        if (value is null) json.Remove(key);
        else json[key] = value;
    }
}
