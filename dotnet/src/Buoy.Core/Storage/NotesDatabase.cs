using Microsoft.Data.Sqlite;

namespace Buoy.Core.Storage;

public enum DatabaseState
{
    /// <summary>Migrated and writable.</summary>
    Ready,
    /// <summary>A newer Buoy has migrated it further than this build knows. Opened read-only.</summary>
    ReadOnlyNewerVersion,
    /// <summary>A migration failed. Nothing is written until it's fixed.</summary>
    MigrationFailed,
}

/// <summary>
/// Opens <c>notes.db</c>: backs it up if migrations are pending, applies them,
/// and adds the sync tables. Mirrors <c>setupDatabase</c> and
/// <c>backUpBeforeMigrating</c> in the Mac's <c>NoteStore</c>.
/// </summary>
public sealed class NotesDatabase : IDisposable
{
    private const int BackupsToKeep = 3;

    public SqliteConnection Connection { get; }
    public DatabaseState State { get; }
    /// <summary>Set when a needed pre-migration backup couldn't be written. The migration still runs, as on the Mac.</summary>
    public bool BackupFailed { get; }
    public Exception? MigrationError { get; }

    public bool IsWritable => State == DatabaseState.Ready;

    private NotesDatabase(SqliteConnection connection, DatabaseState state, bool backupFailed, Exception? migrationError)
    {
        Connection = connection;
        State = state;
        BackupFailed = backupFailed;
        MigrationError = migrationError;
    }

    public static NotesDatabase Open(BuoyPaths paths, string appVersion, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        Directory.CreateDirectory(paths.Root);

        // Pooling off: this one connection lives as long as the app, and a pooled
        // handle would keep the file open after Dispose (backups, tests).
        var connection = new SqliteConnection(ConnectionString(paths.NotesDatabase, readOnly: false));
        connection.Open();

        var status = Migrations.Inspect(connection);
        if (status.IsNewer)
        {
            connection.Dispose();
            var readOnly = new SqliteConnection(ConnectionString(paths.NotesDatabase, readOnly: true));
            readOnly.Open();
            return new NotesDatabase(readOnly, DatabaseState.ReadOnlyNewerVersion, false, null);
        }

        var backupFailed = false;
        if (status.NotesTableExists && status.Pending.Count > 0)
            backupFailed = !TryBackUp(connection, paths, appVersion, clock);

        try
        {
            Migrations.ApplyPending(connection);
            SyncSchema.Ensure(connection);
        }
        catch (SqliteException e)
        {
            return new NotesDatabase(connection, DatabaseState.MigrationFailed, backupFailed, e);
        }

        return new NotesDatabase(connection, DatabaseState.Ready, backupFailed, null);
    }

    private static string ConnectionString(string path, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();

    /// <summary>
    /// Copies the database into <c>backups/notes-before-&lt;version&gt;-&lt;time&gt;.db</c>
    /// with SQLite's online backup (consistent, unlike a file copy) and keeps the newest three.
    /// </summary>
    private static bool TryBackUp(SqliteConnection source, BuoyPaths paths, string appVersion, TimeProvider clock)
    {
        var stamp = clock.GetUtcNow().ToString("yyyy-MM-ddTHH-mm-ssZ");
        var destination = Path.Combine(paths.Backups, $"notes-before-{appVersion}-{stamp}.db");
        try
        {
            Directory.CreateDirectory(paths.Backups);
            using (var backup = new SqliteConnection(ConnectionString(destination, readOnly: false)))
            {
                backup.Open();
                source.BackupDatabase(backup);
            }
            Prune(paths.Backups);
            return true;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            try { File.Delete(destination); } catch (IOException) { }
            return false;
        }
    }

    private static void Prune(string directory)
    {
        var backups = new DirectoryInfo(directory)
            .GetFiles("notes-before-*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal);
        foreach (var old in backups.Skip(BackupsToKeep))
            old.Delete();
    }

    public void Dispose() => Connection.Dispose();
}
