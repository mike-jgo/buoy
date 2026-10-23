namespace Buoy.Core.Storage;

/// <summary>
/// A row of the <c>notes</c> table, field for field the Mac's <c>Note</c>
/// (<c>Buoy/Models/Note.swift</c>).
/// </summary>
public sealed class Note
{
    /// <summary>Milliseconds since the epoch at creation, as a string.</summary>
    public required string Id { get; init; }
    public string Title { get; set; } = "";
    /// <summary>RTF as written by <see cref="Documents.RtfWriter"/> or the Mac. Empty means an empty note.</summary>
    public byte[] ContentRtf { get; set; } = [];
    /// <summary>Milliseconds since the epoch.</summary>
    public long CreatedAt { get; set; }
    /// <summary>Milliseconds since the epoch.</summary>
    public long UpdatedAt { get; set; }
    public bool IsPinned { get; set; }
    public long? PinnedOrder { get; set; }
    /// <summary>Auto-title attempts spent. Windows never auto-titles but keeps the Mac's state intact.</summary>
    public int AutoTitleStage { get; set; }
    /// <summary>True once the user has typed a title; set by every title edit.</summary>
    public bool AutoTitleLocked { get; set; }
    /// <summary>The "Note N" title the note was created with.</summary>
    public string? AutoTitleDefaultTitle { get; set; }
    /// <summary>The folder the note is filed in. Folders group; the note still lists under All Notes.</summary>
    public string? FolderId { get; set; }
    public long? FolderOrder { get; set; }
    /// <summary>Manual position in the All Notes list.</summary>
    public long? SortOrder { get; set; }

    public Note Clone() => (Note)MemberwiseClone();
}

/// <summary>A row of the <c>folders</c> table (<c>Buoy/Models/Folder.swift</c>).</summary>
public sealed class Folder
{
    public const string DefaultName = "New Folder";

    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public long SortOrder { get; set; }
    public bool IsExpanded { get; set; }
    public long CreatedAt { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name.Trim();

    public Folder Clone() => (Folder)MemberwiseClone();
}
