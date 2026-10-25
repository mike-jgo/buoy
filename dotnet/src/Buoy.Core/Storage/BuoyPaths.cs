namespace Buoy.Core.Storage;

/// <summary>
/// Where Buoy keeps its files: <c>%USERPROFILE%\.buoy</c>, the same layout as
/// the Mac's <c>~/.buoy</c>.
/// </summary>
public sealed class BuoyPaths
{
    public string Root { get; }

    public BuoyPaths(string root) => Root = root;

    public static BuoyPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".buoy"));

    /// <summary>
    /// Set <c>BUOY_DATA_DIR</c> to run against another folder: a test profile
    /// that never touches the real notes.
    /// </summary>
    public const string TestProfileVariable = "BUOY_DATA_DIR";

    /// <summary>The folder this run uses: <see cref="TestProfileVariable"/> if set, otherwise <see cref="Default"/>.</summary>
    public static BuoyPaths Current { get; } =
        Environment.GetEnvironmentVariable(TestProfileVariable) is { Length: > 0 } dir ? new BuoyPaths(dir) : Default;

    public bool IsTestProfile => !ReferenceEquals(this, Default);

    public string NotesDatabase => Path.Combine(Root, "notes.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Backups => Path.Combine(Root, "backups");
}
