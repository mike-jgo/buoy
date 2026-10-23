using Microsoft.Data.Sqlite;

namespace Buoy.Core.Storage;

/// <summary>
/// The sync client's bookkeeping, kept out of <c>notes</c> and out of
/// <c>grdb_migrations</c> so the Mac app, which doesn't know about sync yet,
/// still opens this database. The Mac sync client will add the same tables as
/// its <c>v10_sync</c> migration with this same SQL.
/// </summary>
public static class SyncSchema
{
    public static void Ensure(SqliteConnection db)
    {
        db.Execute("""
            CREATE TABLE IF NOT EXISTS sync_notes (
                noteID TEXT PRIMARY KEY NOT NULL,
                baseRev INTEGER NOT NULL DEFAULT 0,
                dirty INTEGER NOT NULL DEFAULT 0,
                doc TEXT
            )
            """);
        db.Execute("""
            CREATE TABLE IF NOT EXISTS sync_folders (
                folderID TEXT PRIMARY KEY NOT NULL,
                baseRev INTEGER NOT NULL DEFAULT 0
            )
            """);
        db.Execute("""
            CREATE TABLE IF NOT EXISTS sync_tombstones (
                id TEXT PRIMARY KEY NOT NULL,
                kind TEXT NOT NULL,
                baseRev INTEGER NOT NULL
            )
            """);
        db.Execute("""
            CREATE TABLE IF NOT EXISTS sync_meta (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT
            )
            """);
    }
}
