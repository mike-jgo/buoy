namespace Buoy.Core.Documents;

/// <summary>What Return does on the current line.</summary>
public abstract record ReturnOutcome
{
    /// <summary>Not a list line: the editor inserts a plain paragraph break.</summary>
    public sealed record NotList : ReturnOutcome;
    /// <summary>Start a new line with <see cref="Marker"/> (editor form) at <see cref="Level"/>.</summary>
    public sealed record Continue(string Marker, int Level) : ReturnOutcome;
    /// <summary>An empty item: remove its marker, and its indent when <see cref="ResetIndent"/>.</summary>
    public sealed record EndList(int MarkerLength, bool ResetIndent) : ReturnOutcome;
}

/// <summary>What Backspace does at the caret.</summary>
public abstract record BackspaceOutcome
{
    public sealed record NotList : BackspaceOutcome;
    /// <summary>The caret is right after a marker: remove it, and the indent when <see cref="ResetIndent"/>.</summary>
    public sealed record RemoveMarker(int MarkerLength, bool ResetIndent) : BackspaceOutcome;
}

/// <summary>
/// The list behaviour of the Mac editor (<c>handleAutoComplete</c>,
/// <c>handleReturn</c>, <c>handleBackspace</c> and <c>handleTab</c> in
/// <c>BuoyTextView.swift</c>) as pure functions of a line's text and level.
/// Text is in editor form: every marker, to-dos included, is followed by
/// exactly one space.
/// </summary>
public static class ListRules
{
    /// <summary>
    /// Space typed after <c>-</c> or <c>[]</c> at the start of a line turns it
    /// into a bullet or a to-do. <paramref name="textBeforeCaret"/> is the line
    /// up to the caret, before the space is inserted.
    /// </summary>
    public static BlockType? AutoComplete(string textBeforeCaret) => textBeforeCaret switch
    {
        "-" => BlockType.Bullet,
        "[]" => BlockType.Todo,
        _ => null,
    };

    /// <summary>Return with the caret after <paramref name="textBeforeCaret"/> on a line at <paramref name="level"/>.</summary>
    public static ReturnOutcome OnReturn(string textBeforeCaret, int level)
    {
        var (type, _, markerLength) = ListMarkers.Detect(textBeforeCaret);
        if (type == BlockType.Paragraph) return new ReturnOutcome.NotList();

        var content = textBeforeCaret[markerLength..];
        if (string.IsNullOrWhiteSpace(content))
            return new ReturnOutcome.EndList(markerLength, ResetIndent: level > 0);

        var marker = type == BlockType.Todo
            ? $"{ListMarkers.Unchecked} "
            : $"{textBeforeCaret[0]} "; // a bullet keeps its own character
        return new ReturnOutcome.Continue(marker, level);
    }

    /// <summary>Backspace with no selection and the caret after <paramref name="textBeforeCaret"/>.</summary>
    public static BackspaceOutcome OnBackspace(string textBeforeCaret)
    {
        if (textBeforeCaret == $"{ListMarkers.Bullet} ")
            return new BackspaceOutcome.RemoveMarker(2, ResetIndent: false);
        if (textBeforeCaret == $"{ListMarkers.NestedBullet} "
            || textBeforeCaret == $"{ListMarkers.Unchecked} "
            || textBeforeCaret == $"{ListMarkers.Checked} ")
            return new BackspaceOutcome.RemoveMarker(2, ResetIndent: true);
        return new BackspaceOutcome.NotList();
    }

    /// <summary>
    /// Pasted lines written as plain-text lists (<c>- item</c>, <c>* item</c>)
    /// become bullets; everything else is left as it is.
    /// </summary>
    public static IEnumerable<BuoyBlock> PromotePlainBullets(IEnumerable<BuoyBlock> blocks) =>
        blocks.Select(block =>
        {
            if (block.Type != BlockType.Paragraph || block.Runs.Count == 0) return block;
            var first = block.Runs[0];
            var text = first.Text.TrimStart();
            if (!(text.StartsWith("- ") || text.StartsWith("* "))) return block;
            var runs = block.Runs.Skip(1).Prepend(first with { Text = text[2..] });
            return new BuoyBlock(BlockType.Bullet, runs);
        });

    /// <summary>
    /// Tab or Shift+Tab on a list line. Returns the new level, or null when the
    /// key isn't the list's (not a list line, or already at the limit).
    /// </summary>
    public static int? OnTab(BlockType type, int level, bool shift)
    {
        if (type == BlockType.Paragraph) return null;
        if (shift) return level > 0 ? level - 1 : null;
        return level < BuoyBlock.MaxLevel ? level + 1 : null;
    }
}
