using Buoy.Core.Documents;
using Buoy.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Buoy.Core.Tests.Storage;

public sealed class NoteStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly NotesDatabase _db;
    private readonly NoteStore _store;

    public NoteStoreTests()
    {
        _db = NotesDatabase.Open(_dir.Paths, "test", _clock);
        _store = new NoteStore(_db, _clock);
    }

    public void Dispose()
    {
        _store.Dispose();
        _db.Dispose();
        _dir.Dispose();
    }

    private static byte[] Rtf(string text) => RtfWriter.Write(new BuoyDoc([BuoyBlock.Paragraph(text)]));
    private static BuoyDoc Doc(string text) => new([BuoyBlock.Paragraph(text)]);

    private string StoredTitle(string id) => _db.Connection.Scalar<string>("SELECT title FROM notes WHERE id = $1", id)!;
    private byte[] StoredContent(string id) =>
        _db.Connection.Query("SELECT contentRTF FROM notes WHERE id = $1", r => r.Blob("contentRTF"), id).Single();
    private (long BaseRev, bool Dirty, string? Doc)? SyncRow(string id) =>
        _db.Connection.Query("SELECT baseRev, dirty, doc FROM sync_notes WHERE noteID = $1",
            r => ((long BaseRev, bool Dirty, string? Doc)?)(r.GetInt64(0), r.GetInt64(1) != 0, r.IsDBNull(2) ? null : r.GetString(2)), id)
        .SingleOrDefault();

    [Fact]
    public void AnEmptyStoreStartsWithNote1()
    {
        var note = Assert.Single(_store.Notes);
        Assert.Same(note, _store.CurrentNote);
        Assert.Equal("Note 1", note.Title);
        Assert.Equal("Note 1", note.AutoTitleDefaultTitle);
        Assert.False(note.AutoTitleLocked);
        Assert.Equal(0, note.SortOrder);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(), note.Id);
        Assert.Equal((0, true, BuoyDoc.Empty.ToJson()), SyncRow(note.Id));
    }

    [Fact]
    public void CreatingNotesNumbersThemAndKeepsIdsUniqueWithinOneMillisecond()
    {
        _store.CreateNote();
        _store.CreateNote("Scratch");

        Assert.Equal(["Note 1", "Note 2", "Scratch"], _store.Notes.Select(n => n.Title));
        Assert.Equal([0L, 1L, 2L], _store.Notes.Select(n => n.SortOrder!.Value));
        Assert.Equal(3, _store.Notes.Select(n => n.Id).Distinct().Count());
        Assert.Equal("Scratch", _store.CurrentNote!.Title);
        Assert.True(_store.CurrentNote.AutoTitleLocked, "an explicitly titled note is never auto-titled");
    }

    [Fact]
    public void ContentIsWrittenAfterOneSecond()
    {
        var id = _store.CurrentNote!.Id;
        _store.SaveContent(Rtf("hello"), Doc("hello"));

        Assert.Equal(Rtf("hello"), _store.CurrentNote.ContentRtf);
        _clock.Advance(TimeSpan.FromSeconds(0.9));
        Assert.Empty(StoredContent(id));
        _clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(Rtf("hello"), StoredContent(id));
        Assert.Equal(Doc("hello").ToJson(), SyncRow(id)!.Value.Doc);
    }

    [Fact]
    public void OnlyTheLastOfQuickEditsIsWritten()
    {
        var id = _store.CurrentNote!.Id;
        _store.SaveContent(Rtf("a"), Doc("a"));
        _clock.Advance(TimeSpan.FromSeconds(0.5));
        _store.SaveContent(Rtf("ab"), Doc("ab"));
        _clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Empty(StoredContent(id));
        _clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(Rtf("ab"), StoredContent(id));
    }

    [Fact]
    public void TitleIsWrittenAfter600msAndLocksAutoTitling()
    {
        var id = _store.CurrentNote!.Id;
        _store.SaveTitle("Groceries");
        Assert.True(_store.CurrentNote.AutoTitleLocked);
        _clock.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal("Note 1", StoredTitle(id));
        _clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal("Groceries", StoredTitle(id));
        Assert.Equal(1, _db.Connection.Scalar<long>("SELECT autoTitleLocked FROM notes WHERE id = $1", id));
    }

    /// <summary>The Mac's 1.4.1 bug: a title typed on A "transferred" to a newly created note.</summary>
    [Fact]
    public void APendingSaveLandsOnTheNoteItWasMadeFor()
    {
        var a = _store.CurrentNote!.Id;
        _store.SaveTitle("Typed on A");
        _store.SaveContent(Rtf("body of A"), Doc("body of A"));
        _clock.Advance(TimeSpan.FromSeconds(0.3));

        _store.CreateNote();
        var b = _store.CurrentNote!.Id;
        _store.SaveTitle("Typed on B");
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal("Typed on A", StoredTitle(a));
        Assert.Equal(Rtf("body of A"), StoredContent(a));
        Assert.Equal("Typed on B", StoredTitle(b));
        Assert.Empty(StoredContent(b));
    }

    [Fact]
    public void SwitchingFlushesFirst()
    {
        var a = _store.CurrentNote!.Id;
        _store.CreateNote();
        _store.SwitchNote(a);
        _store.SaveContent(Rtf("x"), Doc("x"));
        _store.SwitchNote(_store.Notes[1].Id);
        Assert.Equal(Rtf("x"), StoredContent(a));
    }

    [Fact]
    public void TimerSavesArePostedToTheContext()
    {
        var context = new QueueContext();
        using var store = new NoteStore(_db, _clock, context);
        var id = store.CurrentNote!.Id;
        store.SaveTitle("posted");
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Note 1", StoredTitle(id));
        context.RunAll();
        Assert.Equal("posted", StoredTitle(id));
    }

    [Fact]
    public void AFlushBeforeAPostedSaveRunsItOnlyOnce()
    {
        var context = new QueueContext();
        using var store = new NoteStore(_db, _clock, context);
        var id = store.CurrentNote!.Id;
        store.SaveTitle("first");
        _clock.Advance(TimeSpan.FromSeconds(1)); // posted, not yet run
        store.FlushPendingSaves();               // runs it
        _db.Connection.Execute("UPDATE notes SET title = 'changed elsewhere' WHERE id = $1", id);
        context.RunAll();                        // the posted copy must not write again
        Assert.Equal("changed elsewhere", StoredTitle(id));
    }

    [Fact]
    public void DeletingKeepsOneNoteAndMovesToTheNoteInItsPlace()
    {
        _store.CreateNote();
        _store.CreateNote();
        var middle = _store.Notes[1].Id;
        _store.SwitchNote(middle);

        _store.DeleteNote(middle);
        Assert.Equal(["Note 1", "Note 3"], _store.Notes.Select(n => n.Title));
        Assert.Equal("Note 3", _store.CurrentNote!.Title);

        _store.DeleteNote(_store.CurrentNote.Id);
        _store.DeleteNote(_store.CurrentNote!.Id);
        Assert.Single(_store.Notes);
    }

    [Fact]
    public void DeletingAFiledNoteRenumbersItsFolder()
    {
        _store.CreateNote();
        _store.CreateNote();
        _db.Connection.Execute("INSERT INTO folders (id, name, sortOrder, isExpanded, createdAt) VALUES ('f', 'F', 0, 1, 0)");
        for (var i = 0; i < 3; i++)
            _db.Connection.Execute("UPDATE notes SET folderID = 'f', folderOrder = $1 WHERE id = $2", (long)i, _store.Notes[i].Id);
        _db.Connection.Execute("UPDATE sync_notes SET dirty = 0");
        _store.LoadNoteList();

        _store.DeleteNote(_store.Notes[0].Id);

        Assert.Equal([0L, 1L], _store.Notes.Select(n => n.FolderOrder!.Value));
        Assert.All(_store.Notes, n => Assert.True(SyncRow(n.Id)!.Value.Dirty));
    }

    [Fact]
    public void DeletingASyncedNoteLeavesATombstoneAndAnUnsyncedOneDoesNot()
    {
        _store.CreateNote();
        _store.CreateNote();
        var synced = _store.Notes[0].Id;
        var local = _store.Notes[1].Id;
        _db.Connection.Execute("UPDATE sync_notes SET baseRev = 4, dirty = 0 WHERE noteID = $1", synced);

        _store.DeleteNote(synced);
        _store.DeleteNote(local);

        Assert.Equal(
            [$"{synced}:note:4"],
            _db.Connection.Query("SELECT id, kind, baseRev FROM sync_tombstones", r => $"{r.GetString(0)}:{r.GetString(1)}:{r.GetInt64(2)}"));
        Assert.Null(SyncRow(synced));
        Assert.Null(SyncRow(local));
    }

    [Fact]
    public void ASaveScheduledForADeletedNoteDoesNotResurrectIt()
    {
        _store.CreateNote();
        var doomed = _store.CurrentNote!.Id;
        _store.SaveContent(Rtf("bye"), Doc("bye"));
        _store.DeleteNote(doomed);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(_store.Find(doomed));
        Assert.Equal(0, _db.Connection.Scalar<long>("SELECT COUNT(*) FROM notes WHERE id = $1", doomed));
        Assert.Null(SyncRow(doomed));
    }

    [Fact]
    public void PinningAppendsToThePinnedOrderAndUnpinningClearsIt()
    {
        _store.CreateNote();
        var a = _store.Notes[0].Id;
        var b = _store.Notes[1].Id;
        _db.Connection.Execute("UPDATE sync_notes SET dirty = 0");

        _store.TogglePin(b);
        _store.TogglePin(a);
        Assert.Equal((true, 0L), (_store.Find(b)!.IsPinned, _store.Find(b)!.PinnedOrder!.Value));
        Assert.Equal(1L, _store.Find(a)!.PinnedOrder);
        Assert.True(SyncRow(a)!.Value.Dirty);

        _store.TogglePin(b);
        Assert.False(_store.Find(b)!.IsPinned);
        Assert.Null(_store.Find(b)!.PinnedOrder);
    }

    [Fact]
    public void NavigationWrapsAround()
    {
        _store.CreateNote();
        _store.CreateNote();
        _store.SwitchNote(_store.Notes[2].Id);

        _store.NextNote();
        Assert.Equal("Note 1", _store.CurrentNote!.Title);
        Assert.Equal(NavigationDirection.Forward, _store.LastNavigationDirection);
        _store.PreviousNote();
        Assert.Equal("Note 3", _store.CurrentNote!.Title);
        Assert.Equal(NavigationDirection.Backward, _store.LastNavigationDirection);
    }

    [Fact]
    public void RestoreSelectionIgnoresAMissingNote()
    {
        _store.CreateNote();
        var first = _store.Notes[0].Id;
        _store.RestoreSelection(first);
        Assert.Equal(first, _store.CurrentNote!.Id);
        _store.RestoreSelection("gone");
        Assert.Equal(first, _store.CurrentNote!.Id);
    }

    [Fact]
    public void AReadOnlyStoreChangesNothing()
    {
        _store.SaveTitle("Kept");
        _store.Dispose();
        _db.Connection.Execute("INSERT INTO grdb_migrations VALUES ('v10_fromTheFuture')");
        _db.Dispose();

        using var db = NotesDatabase.Open(_dir.Paths, "test", _clock);
        using var store = new NoteStore(db, _clock);
        Assert.True(store.IsReadOnly);
        Assert.Equal("Kept", store.CurrentNote!.Title);

        store.SaveTitle("Changed");
        store.SaveContent(Rtf("x"), Doc("x"));
        store.CreateNote();
        store.TogglePin(store.CurrentNote.Id);
        store.FlushPendingSaves();

        Assert.Single(store.Notes);
        Assert.Equal("Kept", db.Connection.Scalar<string>("SELECT title FROM notes"));
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
        public void RunAll()
        {
            while (_queue.TryDequeue(out var item)) item.Item1(item.Item2);
        }
    }
}
