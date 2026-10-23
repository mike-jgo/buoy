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

    public string NotesDatabase => Path.Combine(Root, "notes.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Backups => Path.Combine(Root, "backups");
}
