using System.Text;
using System.Text.Json;

namespace Buoy.Core.Documents;

public enum BlockType { Paragraph, Bullet, Todo }

/// <summary>A run of text with one set of formatting.</summary>
public sealed record BuoyRun(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikethrough = false,
    string? Link = null)
{
    internal bool SameFormatting(BuoyRun other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline
        && Strikethrough == other.Strikethrough && Link == other.Link;
}

/// <summary>
/// One paragraph. List markers (<c>• </c>, <c>◦ </c>, <c>☐ </c>, <c>☑ </c>) are
/// not part of the runs: they are implied by <see cref="Type"/>,
/// <see cref="Level"/> and <see cref="Checked"/>.
/// </summary>
public sealed class BuoyBlock : IEquatable<BuoyBlock>
{
    public const int MaxLevel = 2;

    public BlockType Type { get; }
    /// <summary>Nesting level, 0–<see cref="MaxLevel"/>. Always 0 for paragraphs.</summary>
    public int Level { get; }
    /// <summary>Only meaningful for to-dos.</summary>
    public bool Checked { get; }
    public IReadOnlyList<BuoyRun> Runs { get; }

    public BuoyBlock(BlockType type, IEnumerable<BuoyRun> runs, int level = 0, bool isChecked = false)
    {
        if (level < 0 || level > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(level));
        Type = type;
        Level = type == BlockType.Paragraph ? 0 : level;
        Checked = type == BlockType.Todo && isChecked;
        Runs = Canonicalize(runs);
    }

    public static BuoyBlock Paragraph(params BuoyRun[] runs) => new(BlockType.Paragraph, runs);
    public static BuoyBlock Paragraph(string text) => new(BlockType.Paragraph, [new BuoyRun(text)]);

    public string PlainText => string.Concat(Runs.Select(r => r.Text));

    /// <summary>Drops empty runs and merges neighbours with equal formatting,
    /// so two documents that read the same compare equal.</summary>
    private static List<BuoyRun> Canonicalize(IEnumerable<BuoyRun> runs)
    {
        var result = new List<BuoyRun>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (run.Text.Contains('\n') || run.Text.Contains('\r'))
                throw new ArgumentException("A run cannot contain a line break; split it into blocks.");
            if (result.Count > 0 && result[^1].SameFormatting(run))
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            else
                result.Add(run);
        }
        return result;
    }

    public bool Equals(BuoyBlock? other) =>
        other is not null && Type == other.Type && Level == other.Level
        && Checked == other.Checked && Runs.SequenceEqual(other.Runs);

    public override bool Equals(object? obj) => Equals(obj as BuoyBlock);
    public override int GetHashCode() => HashCode.Combine(Type, Level, Checked, PlainText);
}

/// <summary>
/// The format notes travel in between devices and the sync server. RTF stays
/// the local storage format on the desktop; this is what everything agrees on.
/// See <c>docs/plans/windows-mvp.md</c> → "Document format: BuoyDoc".
/// </summary>
public sealed class BuoyDoc : IEquatable<BuoyDoc>
{
    public const int CurrentVersion = 1;

    public IReadOnlyList<BuoyBlock> Blocks { get; }

    /// <summary>
    /// Text is the blocks joined by <c>\n</c>. A document whose only block is
    /// an empty paragraph is the empty document, which has no blocks.
    /// </summary>
    public BuoyDoc(IEnumerable<BuoyBlock> blocks)
    {
        var list = blocks.ToList();
        if (list.Count == 1 && list[0].Type == BlockType.Paragraph && list[0].Runs.Count == 0)
            list.Clear();
        Blocks = list;
    }

    public static BuoyDoc Empty { get; } = new([]);

    public string PlainText => string.Join("\n", Blocks.Select(b => b.PlainText));

    /// <summary>
    /// The note as plain text with its list markers (<c>• </c>, <c>◦ </c>,
    /// <c>☐ </c>, <c>☑ </c>), as the Mac's Copy puts it on the clipboard.
    /// </summary>
    public string CopyText => string.Join("\n", Blocks.Select(b => ListMarkers.EditorPrefix(b) + b.PlainText));

    public bool Equals(BuoyDoc? other) => other is not null && Blocks.SequenceEqual(other.Blocks);
    public override bool Equals(object? obj) => Equals(obj as BuoyDoc);
    public override int GetHashCode() => PlainText.GetHashCode();

    // MARK: - JSON

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", CurrentVersion);
            writer.WriteStartArray("blocks");
            foreach (var block in Blocks)
            {
                writer.WriteStartObject();
                writer.WriteString("type", TypeName(block.Type));
                if (block.Type != BlockType.Paragraph)
                    writer.WriteNumber("level", block.Level);
                if (block.Type == BlockType.Todo)
                    writer.WriteBoolean("checked", block.Checked);
                writer.WriteStartArray("runs");
                foreach (var run in block.Runs)
                {
                    writer.WriteStartObject();
                    writer.WriteString("text", run.Text);
                    if (run.Bold) writer.WriteBoolean("b", true);
                    if (run.Italic) writer.WriteBoolean("i", true);
                    if (run.Underline) writer.WriteBoolean("u", true);
                    if (run.Strikethrough) writer.WriteBoolean("s", true);
                    if (run.Link is not null) writer.WriteString("link", run.Link);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Strict parsing, for the server: anything outside the format is an error.
    /// </summary>
    public static BuoyDoc ParseStrict(string json) => Parse(json, strict: true);

    /// <summary>
    /// Lenient parsing, for clients: unknown block types become paragraphs,
    /// unknown fields are ignored, levels are clamped and bad links dropped.
    /// The text is always kept.
    /// </summary>
    public static BuoyDoc ParseLenient(string json) => Parse(json, strict: false);

    private static BuoyDoc Parse(string json, bool strict)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new BuoyDocFormatException("Not valid JSON.", e); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new BuoyDocFormatException("The document must be a JSON object.");
            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number)
                throw new BuoyDocFormatException("Missing version.");
            if (strict && (!version.TryGetInt32(out var v) || v != CurrentVersion))
                throw new BuoyDocFormatException($"Unsupported version {version}.");
            if (!root.TryGetProperty("blocks", out var blocksElement) || blocksElement.ValueKind != JsonValueKind.Array)
                throw new BuoyDocFormatException("Missing blocks.");

            var blocks = new List<BuoyBlock>();
            foreach (var blockElement in blocksElement.EnumerateArray())
                blocks.Add(ParseBlock(blockElement, strict));
            return new BuoyDoc(blocks);
        }
    }

    private static BuoyBlock ParseBlock(JsonElement element, bool strict)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new BuoyDocFormatException("A block must be an object.");

        var typeName = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        BlockType type;
        switch (typeName)
        {
            case "paragraph": type = BlockType.Paragraph; break;
            case "bullet": type = BlockType.Bullet; break;
            case "todo": type = BlockType.Todo; break;
            default:
                if (strict) throw new BuoyDocFormatException($"Unknown block type '{typeName}'.");
                type = BlockType.Paragraph;
                break;
        }

        var level = 0;
        if (element.TryGetProperty("level", out var l))
        {
            if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out level))
            {
                if (strict) throw new BuoyDocFormatException("Level must be an integer.");
                level = 0;
            }
            if (level < 0 || level > BuoyBlock.MaxLevel)
            {
                if (strict) throw new BuoyDocFormatException($"Level {level} is outside 0–{BuoyBlock.MaxLevel}.");
                level = Math.Clamp(level, 0, BuoyBlock.MaxLevel);
            }
        }

        var isChecked = element.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.True;

        var runs = new List<BuoyRun>();
        if (element.TryGetProperty("runs", out var runsElement))
        {
            if (runsElement.ValueKind != JsonValueKind.Array)
                throw new BuoyDocFormatException("Runs must be an array.");
            foreach (var runElement in runsElement.EnumerateArray())
                runs.Add(ParseRun(runElement, strict));
        }
        else if (strict)
        {
            throw new BuoyDocFormatException("A block needs runs.");
        }

        return new BuoyBlock(type, runs, level, isChecked);
    }

    private static BuoyRun ParseRun(JsonElement element, bool strict)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String)
            throw new BuoyDocFormatException("A run needs text.");

        var text = textElement.GetString()!;
        if (text.Contains('\n') || text.Contains('\r'))
        {
            if (strict) throw new BuoyDocFormatException("A run cannot contain a line break.");
            text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }

        string? link = null;
        if (element.TryGetProperty("link", out var linkElement) && linkElement.ValueKind == JsonValueKind.String)
        {
            link = linkElement.GetString();
            if (!IsAllowedLink(link))
            {
                if (strict) throw new BuoyDocFormatException("Link is not an allowed absolute URL.");
                link = null;
            }
        }

        return new BuoyRun(
            text,
            Bold: Flag(element, "b"),
            Italic: Flag(element, "i"),
            Underline: Flag(element, "u"),
            Strikethrough: Flag(element, "s"),
            Link: link);
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static readonly string[] BlockedSchemes = ["javascript", "vbscript", "data", "file"];

    /// <summary>
    /// Any absolute URL except schemes that run code or read local files. The
    /// Mac app accepts custom schemes (mailto:, app links), so this is a
    /// denylist rather than http(s) only.
    /// </summary>
    public static bool IsAllowedLink(string? link) =>
        link is not null
        && link.Length <= 2048
        && Uri.TryCreate(link, UriKind.Absolute, out var uri)
        && !BlockedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    private static string TypeName(BlockType type) => type switch
    {
        BlockType.Bullet => "bullet",
        BlockType.Todo => "todo",
        _ => "paragraph",
    };
}

public sealed class BuoyDocFormatException : Exception
{
    public BuoyDocFormatException(string message, Exception? inner = null) : base(message, inner) { }
}
