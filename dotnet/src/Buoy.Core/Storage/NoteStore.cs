using Buoy.Core.Documents;

namespace Buoy.Core.Storage;

public enum NavigationDirection { None, Forward, Backward }

/// <summary>
/// The notes and the current note, read from and written to <c>notes.db</c>.
/// A port of the Mac's <c>NoteStore</c> (<c>Buoy/Models/NoteStore.swift</c>)
/// without auto-titling, plus sync bookkeeping: every edit a user makes marks
/// the note dirty in <c>sync_notes</c>, and deleting a synced note leaves a
/// tombstone.
///
/// Not thread-safe: use it from the UI thread. Debounced saves fire on a timer
/// and are posted back through the <see cref="SynchronizationContext"/> given
/// to the constructor (or run inline without one, as in tests).
///
/// While the database is read-only (<see cref="IsReadOnly"/>) every write is
/// ignored.
/// </summary>
public sealed class NoteStore : IDisposable
{
    public static readonly TimeSpan ContentSaveDelay = TimeSpan.FromSeconds(1.0);
    public static readonly TimeSpan TitleSaveDelay = TimeSpan.FromSeconds(0.6);

    private readonly NotesDatabase _database;
    private readonly TimeProvider _clock;
    private readonly SynchronizationContext? _context;
    private PendingSave? _pendingContent;
    private PendingSave? _pendingTitle;

    public IReadOnlyList<Note> Notes { get; private set; } = [];
    public IReadOnlyList<Folder> Folders { get; private set; } = [];
    /// <summary>The note being edited; always one of <see cref="Notes"/>.</summary>
    public Note? CurrentNote { get; private set; }
    public NavigationDirection LastNavigationDirection { get; private set; }
    public bool IsReadOnly => !_database.IsWritable;

    /// <summary>Raised after anything in the store changes.</summary>
    public event EventHandler? Changed;

    private Microsoft.Data.Sqlite.SqliteConnection Db => _database.Connection;

    public NoteStore(NotesDatabase database, TimeProvider? clock = null, SynchronizationContext? context = null)
    {
        _database = database;
        _clock = clock ?? TimeProvider.System;
        _context = context;

        if (_database.State == DatabaseState.MigrationFailed) return;
        LoadNoteList();
        if (Notes.Count > 0) SwitchNote(Notes[0].Id);
        else CreateNote();
    }

    private long Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    // MARK: - Reading

    public void LoadNoteList()
    {
        var currentId = CurrentNote?.Id;
        Notes = Db.Query("SELECT * FROM notes ORDER BY sortOrder ASC, createdAt ASC, id ASC", ReadNote);
        Folders = Db.Query("SELECT * FROM folders ORDER BY sortOrder ASC", r => new Folder
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Name = r.GetString(r.GetOrdinal("name")),
            SortOrder = r.GetInt64(r.GetOrdinal("sortOrder")),
            IsExpanded = r.Boolean("isExpanded"),
            CreatedAt = r.GetInt64(r.GetOrdinal("createdAt")),
        });
        CurrentNote = currentId is null ? null : Find(currentId);
        RaiseChanged();
    }

    private static Note ReadNote(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Title = r.GetString(r.GetOrdinal("title")),
        ContentRtf = r.Blob("contentRTF"),
        CreatedAt = r.GetInt64(r.GetOrdinal("createdAt")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("updatedAt")),
        IsPinned = r.Boolean("isPinned"),
        PinnedOrder = r.NullableInt64("pinnedOrder"),
        AutoTitleStage = (int)(r.NullableInt64("autoTitleStage") ?? 0),
        AutoTitleLocked = r.Boolean("autoTitleLocked"),
        AutoTitleDefaultTitle = r.NullableString("autoTitleDefaultTitle"),
        FolderId = r.NullableString("folderID"),
        FolderOrder = r.NullableInt64("folderOrder"),
        SortOrder = r.NullableInt64("sortOrder"),
    };

    public Note? Find(string id) => Notes.FirstOrDefault(n => n.Id == id);

    // MARK: - Selection

    public void SwitchNote(string id)
    {
        FlushPendingSaves();
        var note = Find(id);
        if (note is null) return;
        CurrentNote = note;
        RaiseChanged();
    }

    /// <summary>Reopens the note last open (<c>lastSelectedNoteID</c>), if it still exists.</summary>
    public void RestoreSelection(string? id)
    {
        if (id is not null && Find(id) is not null) SwitchNote(id);
    }

    public void PreviousNote() => Navigate(-1, NavigationDirection.Backward);
    public void NextNote() => Navigate(+1, NavigationDirection.Forward);

    /// <summary>Moves through <see cref="Notes"/>, wrapping at either end.</summary>
    private void Navigate(int step, NavigationDirection direction)
    {
        if (CurrentNote is null || Notes.Count == 0) return;
        var index = Notes.ToList().FindIndex(n => n.Id == CurrentNote.Id);
        if (index < 0) return;
        LastNavigationDirection = direction;
        SwitchNote(Notes[(index + step + Notes.Count) % Notes.Count].Id);
    }

    // MARK: - Creating and deleting

    /// <summary>
    /// Creates a note and makes it current. Without a title it is the next
    /// "Note N", left unlocked so a Mac can still auto-title it.
    /// </summary>
    public void CreateNote(string? title = null)
    {
        // Commit edits to the outgoing note before currentNote moves, so a
        // debounced save can't land on the new one.
        FlushPendingSaves();
        if (IsReadOnly) return;

        var now = Now();
        var defaultTitle = title ?? $"Note {Notes.Count + 1}";
        var id = NewNoteId(now);
        Db.InTransaction(() =>
        {
            Db.Execute("""
                INSERT INTO notes (id, title, contentRTF, createdAt, updatedAt, isPinned, pinnedOrder,
                    autoTitleStage, autoTitleLocked, autoTitleDefaultTitle, folderID, folderOrder, sortOrder)
                VALUES ($1, $2, X'', $3, $3, 0, NULL, 0, $4, $5, NULL, NULL, $6)
                """,
                id, defaultTitle, now, title is not null, defaultTitle, NextSortOrder());
            MarkDirty(id, BuoyDoc.Empty);
        });
        LoadNoteList();
        CurrentNote = Find(id);
        RaiseChanged();
    }

    /// <summary>
    /// The creation time in milliseconds, as the Mac does, bumped past any id
    /// already taken (two notes in one millisecond would otherwise collide).
    /// </summary>
    private string NewNoteId(long now)
    {
        while (Db.Scalar<long>("SELECT COUNT(*) FROM notes WHERE id = $1", now.ToString()) > 0) now++;
        return now.ToString();
    }

    private long NextSortOrder() => (Db.Scalar<long?>("SELECT MAX(sortOrder) FROM notes") ?? -1) + 1;

    /// <summary>Deletes a note, keeping at least one. The current note moves to the one in its place.</summary>
    public void DeleteNote(string id)
    {
        FlushPendingSaves();
        if (IsReadOnly || Notes.Count <= 1) return;
        var note = Find(id);
        if (note is null) return;

        var index = Notes.ToList().FindIndex(n => n.Id == id);
        var wasCurrent = CurrentNote?.Id == id;
        Db.InTransaction(() =>
        {
            Db.Execute("DELETE FROM notes WHERE id = $1", id);
            if (note.FolderId is not null) RenumberFolder(note.FolderId);
            LeaveTombstone(id);
        });

        LoadNoteList();
        if (wasCurrent && Notes.Count > 0)
            SwitchNote(Notes[Math.Min(index, Notes.Count - 1)].Id);
    }

    /// <summary>Keeps a folder's manual order contiguous from 0 after one of its notes goes.</summary>
    private void RenumberFolder(string folderId)
    {
        var ids = Db.Query(
            "SELECT id FROM notes WHERE folderID = $1 ORDER BY folderOrder ASC, createdAt ASC, id ASC",
            r => r.GetString(0), folderId);
        for (var i = 0; i < ids.Count; i++)
        {
            if (Db.Execute("UPDATE notes SET folderOrder = $1 WHERE id = $2 AND folderOrder IS NOT $1", (long)i, ids[i]) > 0)
                MarkDirty(ids[i]);
        }
    }

    // MARK: - Pinning

    public void TogglePin(string id)
    {
        if (IsReadOnly) return;
        var note = Find(id);
        if (note is null) return;

        Db.InTransaction(() =>
        {
            if (note.IsPinned)
            {
                Db.Execute("UPDATE notes SET isPinned = 0, pinnedOrder = NULL WHERE id = $1", id);
            }
            else
            {
                var next = (Db.Scalar<long?>("SELECT MAX(pinnedOrder) FROM notes WHERE isPinned = 1") ?? -1) + 1;
                Db.Execute("UPDATE notes SET isPinned = 1, pinnedOrder = $1 WHERE id = $2", next, id);
            }
            MarkDirty(id);
        });
        LoadNoteList();
    }

    // MARK: - Saving

    /// <summary>
    /// Records the current note's new content now and writes it after
    /// <see cref="ContentSaveDelay"/>. <paramref name="doc"/> is kept for the
    /// next sync push.
    /// </summary>
    public void SaveContent(byte[] rtf, BuoyDoc doc)
    {
        if (IsReadOnly || CurrentNote is not { } note) return;
        // Capture the target now: the write must land on the note being edited,
        // not on whatever is current when the timer fires.
        var noteId = note.Id;
        Schedule(ref _pendingContent, ContentSaveDelay, () => PersistContent(noteId, rtf, doc));
        note.ContentRtf = rtf;
        note.UpdatedAt = Now();
        RaiseChanged();
    }

    /// <summary>
    /// Records the current note's new title now and writes it after
    /// <see cref="TitleSaveDelay"/>. A title the user typed locks out auto-titling.
    /// </summary>
    public void SaveTitle(string title)
    {
        if (IsReadOnly || CurrentNote is not { } note) return;
        var noteId = note.Id;
        Schedule(ref _pendingTitle, TitleSaveDelay, () => PersistTitle(noteId, title));
        note.Title = title;
        note.UpdatedAt = Now();
        note.AutoTitleLocked = true;
        RaiseChanged();
    }

    private void PersistContent(string noteId, byte[] rtf, BuoyDoc doc)
    {
        Db.InTransaction(() =>
        {
            // A note deleted since the save was scheduled updates nothing and stays deleted.
            if (Db.Execute("UPDATE notes SET contentRTF = $1, updatedAt = $2 WHERE id = $3", rtf, Now(), noteId) > 0)
                MarkDirty(noteId, doc);
        });
    }

    private void PersistTitle(string noteId, string title)
    {
        Db.InTransaction(() =>
        {
            if (Db.Execute("UPDATE notes SET title = $1, updatedAt = $2, autoTitleLocked = 1 WHERE id = $3", title, Now(), noteId) > 0)
                MarkDirty(noteId);
        });
    }

    /// <summary>Writes any pending saves now. Call before the current note changes, on hide and on exit.</summary>
    public void FlushPendingSaves()
    {
        _pendingContent?.RunNow();
        _pendingTitle?.RunNow();
    }

    /// <summary>Drops pending saves without writing them, for a note that is about to stop existing.</summary>
    public void CancelPendingSaves()
    {
        _pendingContent?.Cancel();
        _pendingTitle?.Cancel();
    }

    private void Schedule(ref PendingSave? slot, TimeSpan delay, Action persist)
    {
        slot?.Cancel();
        slot = new PendingSave(this, persist);
        slot.Start(delay);
    }

    /// <summary>One debounced write. It runs at most once: on its timer, or earlier through a flush.</summary>
    private sealed class PendingSave(NoteStore store, Action persist)
    {
        private ITimer? _timer;
        private bool _done;

        public void Start(TimeSpan delay) =>
            _timer = store._clock.CreateTimer(_ => OnTimer(), null, delay, Timeout.InfiniteTimeSpan);

        private void OnTimer()
        {
            if (store._context is { } context) context.Post(_ => RunNow(), null);
            else RunNow();
        }

        public void RunNow()
        {
            if (_done) return;
            Cancel();
            persist();
        }

        public void Cancel()
        {
            _done = true;
            _timer?.Dispose();
            if (ReferenceEquals(store._pendingContent, this)) store._pendingContent = null;
            if (ReferenceEquals(store._pendingTitle, this)) store._pendingTitle = null;
        }
    }

    // MARK: - Sync bookkeeping

    /// <summary>
    /// Marks a note as changed since the last push. <paramref name="doc"/>
    /// replaces the stored document when given; otherwise the last one is kept.
    /// </summary>
    private void MarkDirty(string noteId, BuoyDoc? doc = null) =>
        Db.Execute("""
            INSERT INTO sync_notes (noteID, baseRev, dirty, doc) VALUES ($1, 0, 1, $2)
            ON CONFLICT(noteID) DO UPDATE SET dirty = 1, doc = COALESCE($2, doc)
            """,
            noteId, doc?.ToJson());

    /// <summary>A note the server has seen leaves a tombstone for the next push; one it hasn't just goes.</summary>
    private void LeaveTombstone(string noteId)
    {
        var baseRev = Db.Scalar<long?>("SELECT baseRev FROM sync_notes WHERE noteID = $1", noteId) ?? 0;
        if (baseRev > 0)
            Db.Execute("INSERT OR REPLACE INTO sync_tombstones (id, kind, baseRev) VALUES ($1, 'note', $2)", noteId, baseRev);
        Db.Execute("DELETE FROM sync_notes WHERE noteID = $1", noteId);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        FlushPendingSaves();
    }
}
