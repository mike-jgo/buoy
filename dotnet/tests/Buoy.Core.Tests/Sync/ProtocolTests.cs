using System.Text.Json;
using Buoy.Core.Documents;
using Buoy.Core.Sync;

namespace Buoy.Core.Tests.Sync;

public class ProtocolTests
{
    [Fact]
    public void NotesSerializeInCamelCaseWithTheDocNested()
    {
        var doc = JsonDocument.Parse(new BuoyDoc([BuoyBlock.Paragraph("hi")]).ToJson()).RootElement;
        var request = new PutNoteRequest(3, new NoteFields(
            "Title", doc, 1, 2, true, 0, 5, null, null, 0, true, "Note 1"));

        var json = JsonSerializer.Serialize(request, SyncJson.Options);
        Assert.Contains("\"baseRev\":3", json);
        Assert.Contains("\"doc\":{\"v\":1,\"blocks\":[", json);
        Assert.Contains("\"folderId\":null", json);

        var back = JsonSerializer.Deserialize<PutNoteRequest>(json, SyncJson.Options)!;
        Assert.Equal("Title", back.Note.Title);
        Assert.Equal(new BuoyDoc([BuoyBlock.Paragraph("hi")]), BuoyDoc.ParseStrict(back.Note.Doc.GetRawText()));
    }

    [Fact]
    public void ATombstoneHasNoFields()
    {
        var changes = new ChangesResponse(9, false, [new NoteRecord("1", 4, 9, true, null)], []);
        var back = JsonSerializer.Deserialize<ChangesResponse>(JsonSerializer.Serialize(changes, SyncJson.Options), SyncJson.Options)!;
        var note = Assert.Single(back.Notes);
        Assert.True(note.Deleted);
        Assert.Null(note.Note);
    }
}
