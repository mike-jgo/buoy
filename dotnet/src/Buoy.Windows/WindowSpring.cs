using Buoy.Core.Motion;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Buoy.Windows;

/// <summary>
/// Moves and resizes a window on springs, one per edge measure (x, y, width,
/// height), stepped on the display's frame clock. Asking for a new frame while
/// one is in flight re-aims from where the window is and how fast it's going,
/// so spam-toggling bends the motion instead of restarting it: the
/// interruptibility the Mac's fixed-duration frame animations lacked (and
/// guarded with generation counters). Instant under reduced motion.
/// </summary>
internal sealed class WindowSpring
{
    private readonly AppWindow _window;
    private readonly double _dampingRatio;
    private readonly double _response;
    private Spring? _x, _y, _width, _height;
    private TimeSpan _lastFrame;
    private bool _running;
    private Action? _settled;

    public WindowSpring(AppWindow window, double dampingRatio = 1.0, double response = 0.3)
    {
        _window = window;
        _dampingRatio = dampingRatio;
        _response = response;
    }

    public bool IsMoving => _running;

    /// <summary>The frame the window is heading for (its current frame when at rest).</summary>
    public RectInt32 Target => _running && _x is not null
        ? new RectInt32((int)_x.Target, (int)_y!.Target, (int)_width!.Target, (int)_height!.Target)
        : Current();

    /// <summary>Animates to <paramref name="target"/>, from wherever the window is now. <paramref name="settled"/> runs when it arrives.</summary>
    public void AnimateTo(RectInt32 target, Action? settled = null)
    {
        _settled = settled;
        if (Motion.IsReduced)
        {
            Stop();
            _window.MoveAndResize(target);
            Finish();
            return;
        }

        if (!_running || _x is null)
        {
            var now = Current();
            _x = new Spring(now.X, _dampingRatio, _response);
            _y = new Spring(now.Y, _dampingRatio, _response);
            _width = new Spring(now.Width, _dampingRatio, _response);
            _height = new Spring(now.Height, _dampingRatio, _response);
        }
        _x.Retarget(target.X);
        _y!.Retarget(target.Y);
        _width!.Retarget(target.Width);
        _height!.Retarget(target.Height);

        if (_running) return;
        _running = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Stops where it is, without running the settled callback.</summary>
    public void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnFrame;
        _running = false;
    }

    private void OnFrame(object? sender, object e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        var seconds = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : (time - _lastFrame).TotalSeconds;
        _lastFrame = time;
        if (seconds <= 0) return;

        _x!.Step(seconds);
        _y!.Step(seconds);
        _width!.Step(seconds);
        _height!.Step(seconds);
        _window.MoveAndResize(new RectInt32(
            (int)Math.Round(_x.Value), (int)Math.Round(_y.Value),
            (int)Math.Round(_width.Value), (int)Math.Round(_height.Value)));

        if (_x.IsAtRest && _y.IsAtRest && _width.IsAtRest && _height.IsAtRest)
        {
            Stop();
            Finish();
        }
    }

    private void Finish()
    {
        var settled = _settled;
        _settled = null;
        settled?.Invoke();
    }

    private RectInt32 Current() => new(_window.Position.X, _window.Position.Y, _window.Size.Width, _window.Size.Height);
}
