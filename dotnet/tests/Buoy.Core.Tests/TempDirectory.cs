using Buoy.Core.Storage;

namespace Buoy.Core.Tests;

/// <summary>A fresh <c>.buoy</c> folder under the temp directory, deleted afterwards.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "buoy-tests", Guid.NewGuid().ToString("N"));

    public BuoyPaths Paths => new(Path);

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
