using Buoy.Core.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Buoy.Windows.Shell;

/// <summary>A tray menu entry; <c>null</c> in the list is a separator.</summary>
internal sealed record TrayMenuItem(string Label, Action Invoke);

/// <summary>
/// The tray icon, its menu and the global hotkey. They share one hidden
/// top-level window that receives their messages: the panel's own window comes
/// and goes, and a message-only window can't own a popup menu.
/// </summary>
internal sealed unsafe class NativeShell : IDisposable
{
    private const uint TrayCallbackMessage = PInvoke.WM_APP + 1;
    private const uint TrayIconId = 1;
    private const int HotkeyId = 1;
    // Notification-area events with NOTIFYICON_VERSION_4 (shellapi.h).
    private const uint NinSelect = 0x400;
    private const uint NinKeySelect = 0x401;

    private readonly WNDPROC _windowProcedure; // held so the GC doesn't collect the callback
    private readonly HWND _window;
    private readonly uint _taskbarCreatedMessage;
    private readonly HICON _icon;
    private readonly string _tooltip;
    private bool _hotkeyRegistered;

    public event Action? TrayClicked;
    public event Action? HotkeyPressed;

    /// <summary>Built each time the menu opens, so it always reflects the current state.</summary>
    public Func<IReadOnlyList<TrayMenuItem?>> MenuItems { get; set; } = () => [];

    public NativeShell(string iconPath, string tooltip)
    {
        _tooltip = tooltip;
        _windowProcedure = WindowProcedure;
        _taskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");

        var instance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        fixed (char* className = "BuoyShellWindow")
        fixed (char* windowName = "Buoy")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _windowProcedure,
                hInstance = instance,
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(windowClass);
            _window = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, windowName, WINDOW_STYLE.WS_POPUP,
                0, 0, 0, 0, HWND.Null, HMENU.Null, instance, null);
        }

        var size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        fixed (char* path = iconPath)
        {
            _icon = (HICON)(nint)PInvoke.LoadImage(HINSTANCE.Null, path, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_LOADFROMFILE).Value;
        }
        AddTrayIcon();
    }

    /// <summary>Registers the global hotkey, replacing any earlier one. False if another app already holds it.</summary>
    public bool RegisterHotkey(KeyChord chord)
    {
        UnregisterHotkey();
        var modifiers = (HOT_KEY_MODIFIERS)chord.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        _hotkeyRegistered = PInvoke.RegisterHotKey(_window, HotkeyId, modifiers, (uint)chord.VirtualKey);
        return _hotkeyRegistered;
    }

    private void UnregisterHotkey()
    {
        if (!_hotkeyRegistered) return;
        PInvoke.UnregisterHotKey(_window, HotkeyId);
        _hotkeyRegistered = false;
    }

    private void AddTrayIcon()
    {
        var data = IconData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
            | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = TrayCallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &data);

        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &data);
    }

    private NOTIFYICONDATAW IconData() => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _window,
        uID = TrayIconId,
    };

    private LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_HOTKEY && (int)wParam.Value == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            return (LRESULT)0;
        }

        if (message == TrayCallbackMessage)
        {
            // Version 4: the event is in the low word of lParam; the anchor point is in wParam.
            var trayEvent = (uint)(lParam.Value & 0xFFFF);
            if (trayEvent is NinSelect or NinKeySelect)
            {
                TrayClicked?.Invoke();
            }
            else if (trayEvent == PInvoke.WM_CONTEXTMENU)
            {
                var x = (short)(wParam.Value & 0xFFFF);
                var y = (short)((wParam.Value >> 16) & 0xFFFF);
                ShowMenu(x, y);
            }
            return (LRESULT)0;
        }

        // Explorer restarted: the notification area is new and empty.
        if (message == _taskbarCreatedMessage)
        {
            AddTrayIcon();
            return (LRESULT)0;
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = MenuItems();
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is { } item)
                {
                    fixed (char* label = item.Label)
                        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, (nuint)(i + 1), label);
                }
                else
                {
                    PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
                }
            }

            // Without this the menu stays open after clicking elsewhere.
            PInvoke.SetForegroundWindow(_window);
            var chosen = PInvoke.TrackPopupMenuEx(
                menu, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY),
                x, y, _window, null);
            var index = (int)chosen.Value - 1;
            if (index >= 0 && index < items.Count) items[index]?.Invoke();
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        UnregisterHotkey();
        var data = IconData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &data);
        if (!_icon.IsNull) PInvoke.DestroyIcon(_icon);
        PInvoke.DestroyWindow(_window);
    }
}
