using Buoy.Core.Documents;
using Buoy.Core.Storage;

namespace Buoy.Core.Tests.Storage;

public class AllNotesListTests
{
    private static Note N(string id, string title, string text = "", bool pinned = false, long? pinnedOrder = null, long createdAt = 0) => new()
    {
        Id = id,
        Title = title,
        ContentRtf = RtfWriter.Write(new BuoyDoc([BuoyBlock.Paragraph(text)])),
        IsPinned = pinned,
        PinnedOrder = pinnedOrder,
        CreatedAt = createdAt,
    };

    private readonly NotePlainTextCache Text = new(); // per test: ids repeat across tests

    private static string[] Titles(NoteListSection section) => section.Notes.Select(n => n.Title).ToArray();

    [Fact]
    public void WithoutPinsThereIsOneUnlabelledSectionInManualOrder()
    {
        var sections = AllNotesList.Build([N("1", "B"), N("2", "A")], "", Text.Of);
        var only = Assert.Single(sections);
        Assert.Null(only.Header);
        Assert.Equal(["B", "A"], Titles(only));
    }

    [Fact]
    public void PinnedComeFirstInPinOrderAndStayInAllNotes()
    {
        var sections = AllNotesList.Build([
            N("1", "One"),
            N("2", "Two", pinned: true, pinnedOrder: 1),
            N("3", "Three", pinned: true, pinnedOrder: 0),
            N("4", "Four", pinned: true, pinnedOrder: null, createdAt: 5),
        ], null, Text.Of);

        Assert.Equal([AllNotesList.PinnedHeader, AllNotesList.AllNotesHeader], sections.Select(s => s.Header));
        Assert.Equal(["Three", "Two", "Four"], Titles(sections[0]));
        Assert.Equal(["One", "Two", "Three", "Four"], Titles(sections[1]));
    }

    [Fact]
    public void SearchFlattensToMatchesOnTitleOrText()
    {
        var sections = AllNotesList.Build([
            N("1", "Groceries", "eggs and milk", pinned: true, pinnedOrder: 0),
            N("2", "Work", "call about the MILKshake"),
            N("3", "Ideas", "nothing here"),
        ], "  milk ", Text.Of);

        var only = Assert.Single(sections);
        Assert.Null(only.Header);
        Assert.Equal(["Groceries", "Work"], Titles(only));
    }

    [Fact]
    public void NoMatchesIsEmpty()
    {
        Assert.Empty(AllNotesList.Build([N("1", "A", "b")], "zzz", Text.Of));
    }

    [Fact]
    public void TheCacheReDecodesOnlyAfterAnEdit()
    {
        var cache = new NotePlainTextCache();
        var note = N("1", "T", "first");
        Assert.Equal("first", cache.Of(note));

        note.ContentRtf = RtfWriter.Write(new BuoyDoc([BuoyBlock.Paragraph("second")]));
        Assert.Equal("first", cache.Of(note)); // same updatedAt: still cached

        note.UpdatedAt = 1;
        Assert.Equal("second", cache.Of(note));
    }
}
