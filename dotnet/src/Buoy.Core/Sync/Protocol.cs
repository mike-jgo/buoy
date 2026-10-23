using System.Text.Json;
using System.Text.Json.Serialization;

namespace Buoy.Core.Sync;

// The wire format between the apps and Buoy.Server. See docs/plans/windows-mvp.md → "Sync".
// Validation of incoming values lives with the server (Phase 5).

/// <summary>The synced fields of a note. <see cref="Doc"/> is a BuoyDoc; RTF never travels.</summary>
public sealed record NoteFields(
    string Title,
    JsonElement Doc,
    long CreatedAt,
    long UpdatedAt,
    bool IsPinned,
    long? PinnedOrder,
    long? SortOrder,
    string? FolderId,
    long? FolderOrder,
    int AutoTitleStage,
    bool AutoTitleLocked,
    string? AutoTitleDefaultTitle);

public sealed record FolderFields(string Name, long SortOrder, bool IsExpanded, long CreatedAt);

/// <summary>
/// A note as the server has it. <see cref="Rev"/> counts changes to this row;
/// <see cref="Seq"/> orders all changes on the server. A deleted note is a
/// tombstone with no fields.
/// </summary>
public sealed record NoteRecord(string Id, long Rev, long Seq, bool Deleted, NoteFields? Note);

public sealed record FolderRecord(string Id, long Rev, long Seq, bool Deleted, FolderFields? Folder);

/// <summary><c>GET /v1/changes?since=</c>: rows with a higher <c>seq</c>, oldest first. Ask again while <see cref="More"/>.</summary>
public sealed record ChangesResponse(long Seq, bool More, IReadOnlyList<NoteRecord> Notes, IReadOnlyList<FolderRecord> Folders);

/// <summary><c>PUT /v1/notes/{id}</c>. Accepted only if <see cref="BaseRev"/> is the row's current rev (0 creates).</summary>
public sealed record PutNoteRequest(long BaseRev, NoteFields Note);

public sealed record PutFolderRequest(long BaseRev, FolderFields Folder);

/// <summary>The answer to an accepted write.</summary>
public sealed record WriteResult(long Rev, long Seq);

/// <summary>The body of a 409: the row as the server has it now.</summary>
public sealed record NoteConflict(NoteRecord Current);

public sealed record FolderConflict(FolderRecord Current);

public static class SyncJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
