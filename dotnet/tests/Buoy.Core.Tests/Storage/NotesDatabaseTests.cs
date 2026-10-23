using Buoy.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Buoy.Core.Tests.Storage;

public class NotesDatabaseTests
{
    private static readonly string[] MacSchemaColumns =
    [
        "id TEXT notnull=0 default=- pk=1",
        "title TEXT notnull=1 default='' pk=0",
        "content TEXT notnull=1 default='' pk=0",
        "createdAt INTEGER notnull=1 default=- pk=0",
        "updatedAt INTEGER notnull=1 default=- pk=0",
        "contentRTF BLOB notnull=0 default=X'' pk=0",
        "isPinned BOOLEAN notnull=0 default=0 pk=0",
        "pinnedOrder INTEGER notnull=0 default=- pk=0",
        "autoTitleStage INTEGER notnull=0 default=0 pk=0",
        "autoTitleLocked BOOLEAN notnull=0 default=1 pk=0",
        "autoTitleDefaultTitle TEXT notnull=0 default=- pk=0",
        "folderID TEXT notnull=0 default=- pk=0",
        "folderOrder INTEGER notnull=0 default=- pk=0",
        "sortOrder INTEGER notnull=0 default=- pk=0",
    ];

    [Fact]
    public void FreshDatabaseHasTheMacSchemaAndMigrationRecords()
    {
        using var dir = new TempDirectory();
        using var db = NotesDatabase.Open(dir.Paths, "1.0");

        Assert.Equal(DatabaseState.Ready, db.State);
        Assert.Equal(MacSchemaColumns, Describe(db.Connection, "notes"));
        Assert.Equal(
            Migrations.Identifiers.Order(),
            db.Connection.Query("SELECT identifier FROM grdb_migrations", r => r.GetString(0)).Order());
        foreach (var table in new[] { "folders", "sync_notes", "sync_folders", "sync_tombstones", "sync_meta" })
            Assert.True(Migrations.TableExists(db.Connection, table), table);
        // Sync tables are not GRDB migrations; the Mac must not see them as one.
        Assert.DoesNotContain(
            db.Connection.Query("SELECT identifier FROM grdb_migrations", r => r.GetString(0)),
            id => id.Contains("sync"));
        Assert.False(Directory.Exists(dir.Paths.Backups), "a fresh install is never backed up");
    }

    [Fact]
    public void ReopeningIsANoOp()
    {
        using var dir = new TempDirectory();
        NotesDatabase.Open(dir.Paths, "1.0").Dispose();
        using var db = NotesDatabase.Open(dir.Paths, "1.0");
        Assert.Equal(DatabaseState.Ready, db.State);
        Assert.False(Directory.Exists(dir.Paths.Backups));
    }

    [Fact]
    public void V8DatabaseIsBackedUpThenMigratedWithSortOrderFromCreatedAt()
    {
        using var dir = new TempDirectory();
        using (var db = NotesDatabase.Open(dir.Paths, "1.0"))
        {
            InsertNote(db.Connection, "b", createdAt: 200);
            InsertNote(db.Connection, "a", createdAt: 100);
            InsertNote(db.Connection, "c", createdAt: 200);
            // Roll back to v8: the column and its migration record.
            db.Connection.Execute("ALTER TABLE notes DROP COLUMN sortOrder");
            db.Connection.Execute("DELETE FROM grdb_migrations WHERE identifier = 'v9_noteSortOrder'");
        }

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 9, 30, 0, TimeSpan.Zero));
        using (var db = NotesDatabase.Open(dir.Paths, "1.2", clock))
        {
            Assert.Equal(DatabaseState.Ready, db.State);
            Assert.Equal(
                ["a:0", "b:1", "c:2"],
                db.Connection.Query("SELECT id, sortOrder FROM notes ORDER BY sortOrder", r => $"{r.GetString(0)}:{r.GetInt64(1)}"));
        }

        var backup = Assert.Single(Directory.GetFiles(dir.Paths.Backups));
        Assert.Equal("notes-before-1.2-2026-10-23T09-30-00Z.db", Path.GetFileName(backup));
        using var copy = new SqliteConnection($"Data Source={backup};Pooling=False");
        copy.Open();
        Assert.Equal(3, copy.Scalar<long>("SELECT COUNT(*) FROM notes"));
        Assert.DoesNotContain("sortOrder", Describe(copy, "notes").Select(c => c.Split(' ')[0]));
    }

    [Fact]
    public void OnlyTheNewestThreeBackupsAreKept()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Paths.Backups);
        for (var i = 1; i <= 3; i++)
        {
            var old = Path.Combine(dir.Paths.Backups, $"notes-before-0.{i}-x.db");
            File.WriteAllText(old, "");
            File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }
        using (var db = NotesDatabase.Open(dir.Paths, "1.0"))
            db.Connection.Execute("DELETE FROM grdb_migrations WHERE identifier = 'v9_noteSortOrder'");

        NotesDatabase.Open(dir.Paths, "1.1").Dispose();

        var kept = Directory.GetFiles(dir.Paths.Backups).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain("notes-before-0.1-x.db", kept);
        Assert.Contains(kept, name => name!.StartsWith("notes-before-1.1-"));
    }

    [Fact]
    public void PreAutoTitleDatabaseMigratesThroughV6()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Path);
        using (var raw = new SqliteConnection($"Data Source={dir.Paths.NotesDatabase};Pooling=False"))
        {
            raw.Open();
            raw.Execute("""
                CREATE TABLE notes (id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', content TEXT NOT NULL DEFAULT '',
                    createdAt INTEGER NOT NULL, updatedAt INTEGER NOT NULL);
                ALTER TABLE notes ADD COLUMN contentRTF BLOB DEFAULT X'';
                ALTER TABLE notes ADD COLUMN isPinned BOOLEAN DEFAULT 0;
                ALTER TABLE notes ADD COLUMN pinnedOrder INTEGER;
                ALTER TABLE notes ADD COLUMN autoTitlePending BOOLEAN DEFAULT 0;
                CREATE TABLE grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY);
                INSERT INTO grdb_migrations VALUES ('v1_initial'),('v2_contentRTF'),('v3_isPinned'),('v4_pinnedOrder'),('v5_autoTitlePending');
                INSERT INTO notes (id, title, createdAt, updatedAt, autoTitlePending) VALUES ('1', 'Old', 1, 1, 0), ('2', 'Note 2', 2, 2, 1);
                """);
        }

        using var db = NotesDatabase.Open(dir.Paths, "1.0");
        Assert.Equal(DatabaseState.Ready, db.State);
        Assert.Equal(MacSchemaColumns, Describe(db.Connection, "notes"));
        Assert.Equal(
            ["1:locked=1", "2:locked=0"],
            db.Connection.Query("SELECT id, autoTitleLocked FROM notes ORDER BY id", r => $"{r.GetString(0)}:locked={r.GetInt64(1)}"));
    }

    [Fact]
    public void NewerDatabaseOpensReadOnlyAndIsLeftUntouched()
    {
        using var dir = new TempDirectory();
        using (var db = NotesDatabase.Open(dir.Paths, "1.0"))
            db.Connection.Execute("INSERT INTO grdb_migrations VALUES ('v10_fromTheFuture')");
        var before = File.ReadAllBytes(dir.Paths.NotesDatabase);

        using (var db = NotesDatabase.Open(dir.Paths, "1.0"))
        {
            Assert.Equal(DatabaseState.ReadOnlyNewerVersion, db.State);
            Assert.False(db.IsWritable);
            Assert.Throws<SqliteException>(() => db.Connection.Execute("DELETE FROM notes"));
        }

        Assert.Equal(before, File.ReadAllBytes(dir.Paths.NotesDatabase));
        Assert.False(Directory.Exists(dir.Paths.Backups));
    }

    private static void InsertNote(SqliteConnection db, string id, long createdAt) =>
        db.Execute("INSERT INTO notes (id, title, createdAt, updatedAt) VALUES ($1, $1, $2, $2)", id, createdAt);

    private static List<string> Describe(SqliteConnection db, string table) =>
        db.Query(
            $"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}')",
            r => $"{r.GetString(0)} {r.GetString(1)} notnull={r.GetInt64(2)} default={(r.IsDBNull(3) ? "-" : r.GetString(3))} pk={r.GetInt64(4)}");
}
