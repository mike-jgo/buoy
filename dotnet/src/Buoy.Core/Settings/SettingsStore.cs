using Buoy.Core.Storage;

namespace Buoy.Core.Settings;

/// <summary>
/// Holds the live <see cref="AppSettings"/>. A change is announced at once and
/// written to disk 0.25s later, so a slider drag is one write. Call
/// <see cref="Flush"/> on exit. Mirrors the Mac's <c>SettingsStore</c>.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(0.25);

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private ITimer? _pendingWrite;

    public AppSettings Value { get; private set; }

    public event EventHandler? Changed;

    public SettingsStore(BuoyPaths paths, TimeProvider? clock = null)
    {
        _path = paths.Settings;
        _clock = clock ?? TimeProvider.System;
        Value = File.Exists(_path) ? AppSettings.Parse(File.ReadAllText(_path)) : new AppSettings();
    }

    public void Update(Action<AppSettings> change)
    {
        // The write timer serialises Value on another thread; change it under the same lock.
        lock (_gate)
        {
            change(Value);
            _pendingWrite?.Dispose();
            _pendingWrite = _clock.CreateTimer(_ => Flush(), null, WriteDelay, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes a pending change now. The file is replaced atomically.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_pendingWrite is null) return;
            _pendingWrite.Dispose();
            _pendingWrite = null;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, Value.ToJson());
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Dispose() => Flush();
}
