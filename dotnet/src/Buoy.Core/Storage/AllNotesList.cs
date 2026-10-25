using Buoy.Core.Documents;

namespace Buoy.Core.Storage;

/// <summary>One labelled run of rows in All Notes. <see cref="Header"/> is null when there's only one section.</summary>
public sealed record NoteListSection(string? Header, IReadOnlyList<Note> Notes);

/// <summary>
/// What All Notes shows (the Mac's <c>AllNotesPanel</c> without folders, which
/// the MVP keeps but doesn't show): Pinned in pin order, then every note in
/// manual order, pinned ones included. Headers appear only when both sections
/// do. A search flattens everything into the matches, in manual order.
/// </summary>
public static class AllNotesList
{
    public const string PinnedHeader = "Pinned";
    public const string AllNotesHeader = "All Notes";

    /// <param name="notes">The store's notes, already in manual order.</param>
    /// <param name="query">The search text; blank shows every note.</param>
    /// <param name="plainText">A note's text, for search. Use <see cref="NotePlainTextCache"/>.</param>
    public static IReadOnlyList<NoteListSection> Build(IReadOnlyList<Note> notes, string? query, Func<Note, string> plainText)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            var matches = notes
                .Where(n => n.Title.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                    || plainText(n).Contains(needle, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            return matches.Count == 0 ? [] : [new NoteListSection(null, matches)];
        }

        var pinned = notes
            .Where(n => n.IsPinned)
            .OrderBy(n => n.PinnedOrder ?? long.MaxValue)
            .ThenBy(n => n.CreatedAt)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        return pinned.Count == 0
            ? [new NoteListSection(null, notes)]
            : [new NoteListSection(PinnedHeader, pinned), new NoteListSection(AllNotesHeader, notes)];
    }
}

/// <summary>
/// A note's text, decoded once per edit: keyed on id and <c>updatedAt</c>, so
/// search doesn't re-read every note's RTF on every keystroke (the Mac's
/// <c>NotePlainText</c> rule).
/// </summary>
public sealed class NotePlainTextCache
{
    private readonly Dictionary<string, (long UpdatedAt, string Text)> _cache = [];

    public string Of(Note note)
    {
        if (_cache.TryGetValue(note.Id, out var hit) && hit.UpdatedAt == note.UpdatedAt) return hit.Text;
        var text = RtfPlainText.Extract(note.ContentRtf);
        _cache[note.Id] = (note.UpdatedAt, text);
        return text;
    }
}
