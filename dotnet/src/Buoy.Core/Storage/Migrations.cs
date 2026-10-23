using Microsoft.Data.Sqlite;

namespace Buoy.Core.Storage;

/// <summary>
/// The Mac app's GRDB migrations, replayed with the same SQL in the same
/// order (<c>NoteStore.runMigrations()</c> in <c>Buoy/Models/NoteStore.swift</c>),
/// and recorded in GRDB's own <c>grdb_migrations</c> table so a Mac opening
/// this database sees them as done.
///
/// A new Mac migration must be added here too, under the same identifier.
/// Until it is, this build opens such a database read-only.
/// </summary>
public static class Migrations
{
    private sealed record Migration(string Identifier, Action<SqliteConnection> Apply);

    private static readonly Migration[] All =
    [
        new("v1_initial", db => db.Execute("""
            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT '',
                content TEXT NOT NULL DEFAULT '',
                createdAt INTEGER NOT NULL,
                updatedAt INTEGER NOT NULL
            )
            """)),

        new("v2_contentRTF", db => AddColumnIfMissing(db, "contentRTF", "BLOB DEFAULT X''")),

        new("v3_isPinned", db => AddColumnIfMissing(db, "isPinned", "BOOLEAN DEFAULT 0")),

        new("v4_pinnedOrder", db =>
        {
            AddColumnIfMissing(db, "pinnedOrder", "INTEGER");
            var next = (db.Scalar<long?>("SELECT MAX(pinnedOrder) FROM notes WHERE isPinned = 1") ?? -1) + 1;
            var unordered = db.Query(
                "SELECT id FROM notes WHERE isPinned = 1 AND pinnedOrder IS NULL ORDER BY createdAt ASC, id ASC",
                r => r.GetString(0));
            foreach (var id in unordered)
                db.Execute("UPDATE notes SET pinnedOrder = $1 WHERE id = $2", next++, id);
        }),

        new("v5_autoTitlePending", db => AddColumnIfMissing(db, "autoTitlePending", "BOOLEAN DEFAULT 0")),

        new("v6_autoTitleStages", db =>
        {
            var columns = Columns(db);
            AddColumnIfMissing(db, "autoTitleStage", "INTEGER DEFAULT 0");
            // Existing rows default locked: auto-titling only targets notes created after this.
            AddColumnIfMissing(db, "autoTitleLocked", "BOOLEAN DEFAULT 1");
            AddColumnIfMissing(db, "autoTitleDefaultTitle", "TEXT");
            if (columns.Contains("autoTitlePending"))
            {
                db.Execute("UPDATE notes SET autoTitleLocked = NOT autoTitlePending");
                db.Execute("ALTER TABLE notes DROP COLUMN autoTitlePending");
            }
        }),

        new("v7_autoTitleRestage", db => db.Execute("UPDATE notes SET autoTitleStage = 3 WHERE autoTitleStage >= 2")),

        new("v8_folders", db =>
        {
            db.Execute("""
                CREATE TABLE IF NOT EXISTS folders (
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    sortOrder INTEGER NOT NULL,
                    isExpanded INTEGER NOT NULL DEFAULT 0,
                    createdAt INTEGER NOT NULL
                )
                """);
            AddColumnIfMissing(db, "folderID", "TEXT");
            AddColumnIfMissing(db, "folderOrder", "INTEGER");
        }),

        new("v9_noteSortOrder", db =>
        {
            AddColumnIfMissing(db, "sortOrder", "INTEGER");
            var ordered = db.Query("SELECT id FROM notes ORDER BY createdAt ASC, id ASC", r => r.GetString(0));
            for (var index = 0; index < ordered.Count; index++)
                db.Execute("UPDATE notes SET sortOrder = $1 WHERE id = $2", (long)index, ordered[index]);
        }),
    ];

    public static IReadOnlyList<string> Identifiers { get; } = All.Select(m => m.Identifier).ToArray();

    public sealed record Status(bool NotesTableExists, IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown)
    {
        /// <summary>A newer Buoy has migrated this database further than this build knows how to.</summary>
        public bool IsNewer => Unknown.Count > 0;
    }

    public static Status Inspect(SqliteConnection db)
    {
        var applied = TableExists(db, "grdb_migrations")
            ? db.Query("SELECT identifier FROM grdb_migrations", r => r.GetString(0)).ToHashSet()
            : [];
        return new Status(
            NotesTableExists: TableExists(db, "notes"),
            Pending: Identifiers.Where(id => !applied.Contains(id)).ToList(),
            Unknown: applied.Where(id => !Identifiers.Contains(id)).Order().ToList());
    }

    /// <summary>Applies every pending migration, each in its own transaction, as GRDB does.</summary>
    public static void ApplyPending(SqliteConnection db)
    {
        db.Execute("CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY)");
        var pending = Inspect(db).Pending.ToHashSet();
        foreach (var migration in All.Where(m => pending.Contains(m.Identifier)))
        {
            db.InTransaction(() =>
            {
                migration.Apply(db);
                db.Execute("INSERT INTO grdb_migrations (identifier) VALUES ($1)", migration.Identifier);
            });
        }
    }

    public static bool TableExists(SqliteConnection db, string table) =>
        db.Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $1", table) > 0;

    private static HashSet<string> Columns(SqliteConnection db) =>
        db.Query("SELECT name FROM pragma_table_info('notes')", r => r.GetString(0)).ToHashSet();

    private static void AddColumnIfMissing(SqliteConnection db, string column, string definition)
    {
        if (!Columns(db).Contains(column))
            db.Execute($"ALTER TABLE notes ADD COLUMN \"{column}\" {definition}");
    }
}
