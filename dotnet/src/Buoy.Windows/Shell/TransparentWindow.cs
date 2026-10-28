using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Buoy.Windows.Shell;

/// <summary>
/// Turns a WinUI window into a borderless, per-pixel transparent canvas, so
/// only what the app draws shows. The Mac panel's window is likewise larger
/// than its glass (glassEdgeInset) and the glass moves inside it: folding into
/// Harbor Mode or growing then never resizes the window frame by frame, which
/// is what made those animations hitch.
///
/// The transparent margin passes clicks through to whatever is behind it:
/// Windows hit-tests by rectangle, not by pixel, so the window is made
/// click-through (WS_EX_TRANSPARENT) whenever the pointer is outside
/// <see cref="HitRegions"/>, checked every frame while it is shown.
/// </summary>
internal sealed class TransparentWindow
{
    private const uint SubclassId = 1;

    private readonly HWND _hwnd;
    private readonly SUBCLASSPROC _subclass;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _tracker;
    private bool _passThrough;

    /// <summary>The smallest the window may be resized to, in pixels.</summary>
    public SizeInt32 MinimumSize { get; set; }

    /// <summary>Where the window takes the pointer, in screen pixels; it lets clicks through everywhere else.</summary>
    public Func<IEnumerable<RectInt32>> HitRegions { get; set; } = () => [];

    public unsafe TransparentWindow(HWND hwnd, Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        _hwnd = hwnd;

        // Per-pixel alpha: blur-behind over an empty region is the switch for it.
        var nowhere = PInvoke.CreateRectRgn(-2, -2, -1, -1);
        PInvoke.DwmEnableBlurBehindWindow(hwnd, new DWM_BLURBEHIND
        {
            dwFlags = PInvoke.DWM_BB_ENABLE | PInvoke.DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = nowhere,
        });
        PInvoke.DeleteObject(nowhere);

        // No Windows 11 border or rounding: the glass draws its own corners.
        var noBorder = 0xFFFFFFFE; // DWMWA_COLOR_NONE
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &noBorder, sizeof(uint));
        var square = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &square, sizeof(DWM_WINDOW_CORNER_PREFERENCE));

        _subclass = OnMessage; // held: the native side keeps only a pointer
        PInvoke.SetWindowSubclass(hwnd, _subclass, SubclassId, 0);
        // Recalculate the frame now that WM_NCCALCSIZE gives it all to the client.
        PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

        _tracker = queue.CreateTimer();
        _tracker.Interval = TimeSpan.FromMilliseconds(16);
        _tracker.Tick += (_, _) => UpdatePassThrough();
    }

    /// <summary>Starts or stops following the pointer; only needed while the window is shown.</summary>
    public void Track(bool on)
    {
        if (on) _tracker.Start();
        else _tracker.Stop();
    }

    private void UpdatePassThrough()
    {
        // Mid-press (a drag, a resize, a selection) keeps whatever the press began on.
        if (IsDown(VIRTUAL_KEY.VK_LBUTTON) || IsDown(VIRTUAL_KEY.VK_RBUTTON) || IsDown(VIRTUAL_KEY.VK_MBUTTON)) return;
        PInvoke.GetCursorPos(out var pointer);
        var inside = HitRegions().Any(r =>
            pointer.X >= r.X && pointer.X < r.X + r.Width && pointer.Y >= r.Y && pointer.Y < r.Y + r.Height);
        SetPassThrough(!inside);
    }

    private static bool IsDown(VIRTUAL_KEY key) => (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

    private void SetPassThrough(bool passThrough)
    {
        if (passThrough == _passThrough) return;
        _passThrough = passThrough;
        const int flags = (int)(WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED);
        var style = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, passThrough ? style | flags : style & ~flags);
        // A layered window draws nothing until it has attributes; full opacity changes nothing.
        if (passThrough) PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    private unsafe LRESULT OnMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        switch (message)
        {
            // The whole window is client area: no frame, visible or invisible. The
            // presenter re-adds a dialog frame (a 1px outline, 3px of inset) on resize.
            case PInvoke.WM_NCCALCSIZE when wParam.Value != 0:
                return new LRESULT(0);
            case PInvoke.WM_GETMINMAXINFO when MinimumSize.Width > 0:
                var info = (MINMAXINFO*)lParam.Value;
                info->ptMinTrackSize.X = MinimumSize.Width;
                info->ptMinTrackSize.Y = MinimumSize.Height;
                return new LRESULT(0);
        }
        return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
    }
}
