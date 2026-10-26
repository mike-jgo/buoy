namespace Buoy.Core.Harbor;

/// <summary>
/// One note's Harbor countdown (the Mac's <c>HarborTimer</c>): in memory only,
/// it survives leaving Harbor Mode. Ticks every 0.2s for a prompt finish but
/// raises <see cref="Changed"/> only when the displayed second changes.
/// Ticks are posted to the <see cref="SynchronizationContext"/> it was given
/// (or run inline without one, as in tests).
/// </summary>
public sealed class HarborCountdown : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.2);

    private readonly TimeProvider _clock;
    private readonly SynchronizationContext? _context;
    private ITimer? _ticker;
    private DateTimeOffset? _deadline;
    private TimeSpan _pausedRemaining;

    public bool IsActive { get; private set; }
    public bool IsPaused { get; private set; }
    public int RemainingSeconds { get; private set; }
    public bool IsFinished => IsActive && RemainingSeconds == 0;

    /// <summary><c>4:59</c>, <c>1:02:03</c>, or <c>Time’s up</c>.</summary>
    public string DisplayText
    {
        get
        {
            if (IsFinished) return "Time’s up";
            var hours = RemainingSeconds / 3600;
            var minutes = RemainingSeconds % 3600 / 60;
            var seconds = RemainingSeconds % 60;
            return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
        }
    }

    /// <summary>The displayed time or state changed.</summary>
    public event EventHandler? Changed;
    /// <summary>Reached zero: the moment for the chime.</summary>
    public event EventHandler? Finished;

    public HarborCountdown(TimeProvider? clock = null, SynchronizationContext? context = null)
    {
        _clock = clock ?? TimeProvider.System;
        _context = context;
    }

    /// <summary>Starts counting down from <paramref name="title"/> if it is a duration and nothing is running. True if it started.</summary>
    public bool Start(string title)
    {
        if (IsActive) return false;
        Stop();
        if (HarborDuration.Parse(title) is not { } seconds) return false;
        IsActive = true;
        RemainingSeconds = (int)Math.Ceiling(seconds);
        _deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(seconds);
        StartTicker();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void TogglePause()
    {
        if (!IsActive || IsFinished) return;
        if (IsPaused)
        {
            _deadline = _clock.GetUtcNow() + _pausedRemaining;
            IsPaused = false;
            StartTicker();
        }
        else
        {
            Update();
            if (IsFinished || _deadline is not { } deadline) return;
            _pausedRemaining = deadline - _clock.GetUtcNow();
            _deadline = null;
            IsPaused = true;
            StopTicker();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        var wasActive = IsActive;
        StopTicker();
        _deadline = null;
        _pausedRemaining = TimeSpan.Zero;
        IsActive = false;
        IsPaused = false;
        RemainingSeconds = 0;
        if (wasActive) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StartTicker()
    {
        StopTicker();
        _ticker = _clock.CreateTimer(_ => OnTick(), null, TickInterval, TickInterval);
    }

    private void StopTicker()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    private void OnTick()
    {
        if (_context is { } context) context.Post(_ => Update(), null);
        else Update();
    }

    private void Update()
    {
        if (_deadline is not { } deadline) return;
        var next = Math.Max(0, (int)Math.Ceiling((deadline - _clock.GetUtcNow()).TotalSeconds));
        if (next == RemainingSeconds) return;
        RemainingSeconds = next;
        if (next == 0)
        {
            StopTicker();
            _deadline = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (next == 0) Finished?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => StopTicker();
}
