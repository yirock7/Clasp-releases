using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Resources;
using Microsoft.Win32;
using static AppleDeviceHub.App.Shell.NativeMethods;

namespace AppleDeviceHub.App.Shell;

/// <summary>Where a tray activation happened, in physical screen pixels.</summary>
internal readonly record struct TrayActivation(int X, int Y, bool ByKeyboard);

/// <summary>What the tray menu's info and connect lines say when it opens.</summary>
internal sealed record TrayMenuState(string BatteryText, string ConnectText, bool ConnectEnabled);

/// <summary>
/// Notification-area icon built directly on Shell_NotifyIcon (NOTIFYICON_VERSION_4), so the
/// flyout can anchor to the icon's real rectangle (Shell_NotifyIconGetRect). Left click and
/// keyboard selection (Win+B, arrows, Enter) raise <see cref="Activated"/>; right click or the
/// menu key opens a native Win32 menu (battery line, Connect/Disconnect, Open, Settings, Quit),
/// which screen readers and keyboards already understand.
/// Must be created and used on the UI thread (its hidden window shares the UI message loop).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string WindowClass = "Clasp.TrayWindow";
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;

    private const int CmdOpen = 1, CmdConnectToggle = 2, CmdSettings = 3, CmdQuit = 4;
    private const uint MF_GRAYED = 0x1;

    private readonly WndProc _wndProc; // kept alive: the native window calls it
    private readonly uint _taskbarCreated;
    private readonly nint _hwnd;
    private nint _hIcon;
    private string _tooltip;
    private bool _badged; // _hIcon is a caller-supplied badge icon, not the packaged theme icon
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = Clip(tooltip);
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClass,
        };
        RegisterClassExW(ref wc); // fails harmlessly if already registered in this process

        // A hidden top-level window (not message-only) so it also receives the TaskbarCreated
        // broadcast after Explorer restarts, and theme changes.
        _hwnd = CreateWindowExW((uint)WS_EX_TOOLWINDOW, WindowClass, "Clasp tray", WS_POPUP,
            0, 0, 0, 0, 0, 0, hInstance, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"Tray window creation failed ({Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Left click or keyboard selection on the icon.</summary>
    public event EventHandler<TrayActivation>? Activated;

    /// <summary>Asked each time the menu opens; null (or no handler) omits the battery and connect lines.</summary>
    public Func<TrayMenuState>? MenuState { get; set; }

    public event EventHandler? OpenRequested;
    public event EventHandler? ConnectToggleRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;

    /// <summary>
    /// The taskbar theme changed while a badge icon is shown. The owner should render the badge
    /// again and call <see cref="SetIcon"/>; the packaged icon is not restored on its own.
    /// </summary>
    public event EventHandler? ThemeChanged;

    public void Show()
    {
        LoadIconForTheme();
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    public void SetTooltip(string text)
    {
        _tooltip = Clip(text);
        if (!_added) return;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Shows <paramref name="hIcon"/> (for example from <see cref="TrayIconRenderer"/>). The tray
    /// icon takes ownership and destroys the handle when it is replaced or disposed; the caller
    /// must not destroy it. A zero handle is ignored.
    /// </summary>
    public void SetIcon(nint hIcon)
    {
        if (hIcon == 0 || _disposed) return;
        var old = _hIcon;
        _hIcon = hIcon;
        _badged = true;
        if (_added)
        {
            var data = Data(NIF_ICON);
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        if (old != 0 && old != hIcon) DestroyIcon(old);
    }

    /// <summary>Goes back to the packaged, theme-matched icon.</summary>
    public void ResetIcon()
    {
        if (_disposed || !_badged) return;
        _badged = false;
        LoadIconForTheme();
        if (_added)
        {
            var data = Data(NIF_ICON);
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    /// <summary>
    /// The icon's screen rectangle, or null when Windows cannot provide it (for example while
    /// the icon sits in a closed overflow area). Callers then fall back to the cursor.
    /// </summary>
    public PixelRect? GetIconRect()
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = IconId,
        };
        if (Shell_NotifyIconGetRect(ref id, out var rect) != 0) return null;
        var r = rect.ToPixelRect();
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }
        if (_hIcon != 0) DestroyIcon(_hIcon);
        DestroyWindow(_hwnd);
    }

    // ---- internals ---------------------------------------------------------------------

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
                int x = (short)(wParam.ToInt64() & 0xFFFF);
                int y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                switch (evt)
                {
                    case NIN_SELECT:
                        Activated?.Invoke(this, new TrayActivation(x, y, ByKeyboard: false));
                        break;
                    case NIN_KEYSELECT:
                        Activated?.Invoke(this, new TrayActivation(x, y, ByKeyboard: true));
                        break;
                    case WM_CONTEXTMENU:
                        ShowMenu(x, y);
                        break;
                }
                return 0;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                _added = false;
                Show(); // Explorer restarted: the icon must be added again
                return 0;
            }
            if (msg == WM_SETTINGCHANGE)
            {
                // Taskbar light/dark may have changed ("ImmersiveColorSet"); refresh the icon.
                if (_badged)
                {
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                    return DefWindowProcW(hWnd, msg, wParam, lParam);
                }
                LoadIconForTheme();
                if (_added)
                {
                    var data = Data(NIF_ICON);
                    Shell_NotifyIconW(NIM_MODIFY, ref data);
                }
            }
        }
        catch
        {
            // Never let an exception cross the native callback boundary.
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            TrayMenuState? state = null;
            try { state = MenuState?.Invoke(); }
            catch { /* the menu still works without the dynamic lines */ }
            if (state is not null)
            {
                AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, EscapeMenuText(state.BatteryText));
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                AppendMenuW(menu, state.ConnectEnabled ? MF_STRING : MF_STRING | MF_GRAYED, CmdConnectToggle, EscapeMenuText(state.ConnectText));
            }
            AppendMenuW(menu, MF_STRING, CmdOpen, Strings.Tray_Menu_Open);
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdSettings, Strings.Tray_Menu_Settings);
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdQuit, Strings.Tray_Menu_Quit);

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            uint align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_BOTTOMALIGN | align, x, y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);

            switch (cmd)
            {
                case CmdOpen: OpenRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdConnectToggle: ConnectToggleRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdSettings: SettingsRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdQuit: QuitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>A literal ampersand in menu text would otherwise become an accelerator underline.</summary>
    private static string EscapeMenuText(string text) => text.Replace("&", "&&");

    private void LoadIconForTheme()
    {
        if (_badged) return; // a badge icon is in use; its owner re-renders on ThemeChanged
        var file = TaskbarUsesLightTheme() ? "TrayIcon-Light.ico" : "TrayIcon-Dark.ico";
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        uint dpi = GetDpiForSystem();
        int cx = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        int cy = GetSystemMetricsForDpi(SM_CYSMICON, dpi);
        var icon = LoadImageW(0, path, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
        if (icon == 0) return; // keep the previous icon
        if (_hIcon != 0) DestroyIcon(_hIcon);
        _hIcon = icon;
    }

    /// <summary>Read-only check of the taskbar theme; defaults to dark (the Windows 11 default).</summary>
    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>NOTIFYICONDATA tooltips hold at most 127 characters.</summary>
    private static string Clip(string text) => text.Length > 127 ? text[..127] : text;
}
