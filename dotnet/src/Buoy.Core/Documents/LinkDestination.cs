namespace Buoy.Core.Documents;

/// <summary>What the link dialog makes of a typed address (the Mac's <c>LinkDestination</c>).</summary>
public static class LinkDestination
{
    /// <summary>
    /// <c>example.com</c> becomes <c>https://example.com</c>; an explicit scheme
    /// (<c>mailto:</c>, app links) is kept. Returns null for an empty or unusable
    /// address, a web address without a host, or a scheme BuoyDoc doesn't allow.
    /// </summary>
    public static string? Normalize(string? input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        // "localhost:3000" and "example.com:8080" parse as schemes; they're web hosts.
        var colon = trimmed.IndexOf(':');
        var scheme = colon > 0 ? trimmed[..colon] : "";
        var schemeless = scheme.Length == 0
            || scheme.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || scheme.Contains('.');
        var candidate = schemeless ? $"https://{trimmed}" : trimmed;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Host)) return null;
        return BuoyDoc.IsAllowedLink(candidate) ? candidate : null;
    }
}
