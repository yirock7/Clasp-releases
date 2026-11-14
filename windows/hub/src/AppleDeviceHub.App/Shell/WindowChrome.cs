using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using AppleDeviceHub.App.Design;
using static AppleDeviceHub.App.Shell.NativeMethods;

namespace AppleDeviceHub.App.Shell;

/// <summary>Shared window set-up for the floating glass surfaces.</summary>
internal static class WindowChrome
{
    /// <summary>
    /// Borderless, always-on-top, hidden from Alt+Tab and the taskbar, DWM-rounded corners and
    /// no DWM accent border (the view draws its own luminous edge). Non-activatable windows
    /// never take keyboard focus, so they cannot interrupt typing.
    /// </summary>
    public static void MakeFloating(Window window, bool activatable)
    {
        var appWindow = window.AppWindow;
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            // Apply this after the other presenter flags: changing IsResizable can restore the
            // native dialog-frame style even when the border was cleared earlier.
            p.SetBorderAndTitleBar(false, false);
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        nint ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (!activatable) ex |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex);
        RemoveNativeFrame(hwnd);

        // Both attributes are cosmetic: on failure (e.g. an older Windows build) the window keeps
        // square corners or a border, so log it and carry on.
        int corner = DWMWCP_ROUND;
        int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        if (hr < 0) LocalLog.Write($"DWM corner preference failed: 0x{hr:X8}");
        int border = DWMWA_COLOR_NONE;
        hr = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        if (hr < 0) LocalLog.Write($"DWM border colour failed: 0x{hr:X8}");
    }

    /// <summary>
    /// WinUI can retain or restore native frame styles after the presenter border is disabled.
    /// Clear only frame-related bits, then ask Windows to recalculate the frame without moving,
    /// resizing, changing z-order, changing visibility, or activating the window.
    /// </summary>
    public static void RemoveNativeFrame(nint hwnd)
    {
        if (hwnd == 0) return;

        nint style = GetWindowLongPtr(hwnd, GWL_STYLE);
        nint extendedStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        nint borderlessStyle = style & ~(WS_BORDER | WS_DLGFRAME | WS_THICKFRAME);
        nint borderlessExtendedStyle = extendedStyle &
            ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME);
        bool changed = false;

        if (borderlessStyle != style)
            changed |= TrySetWindowStyle(hwnd, GWL_STYLE, borderlessStyle, "style");
        if (borderlessExtendedStyle != extendedStyle)
            changed |= TrySetWindowStyle(hwnd, GWL_EXSTYLE, borderlessExtendedStyle, "extended style");

        if (!changed) return;

        Marshal.SetLastPInvokeError(0);
        if (!SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOOWNERZORDER |
            SWP_NOACTIVATE | SWP_FRAMECHANGED))
        {
            SafeLogFrameFailure($"Native frame refresh failed: Win32 error {Marshal.GetLastPInvokeError()}.");
        }
    }

    private static bool TrySetWindowStyle(nint hwnd, int index, nint value, string styleName)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = SetWindowLongPtr(hwnd, index, value);
        int error = Marshal.GetLastPInvokeError();
        if (previous != 0 || error == 0) return true;

        SafeLogFrameFailure($"Native frame {styleName} update failed: Win32 error {error}.");
        return false;
    }

    private static void SafeLogFrameFailure(string message)
    {
        try { LocalLog.Write(message); }
        catch { /* Cosmetic native frame cleanup must never interrupt window startup. */ }
    }

    /// <summary>
    /// Desktop Acrylic (real blur of what is behind the window) unless transparency is reduced,
    /// in which case there is no backdrop and the view draws opaque surfaces.
    /// </summary>
    public static void ApplyFloatingBackdrop(Window window)
    {
        try
        {
            if (EffectsPolicy.ReduceTransparency)
            {
                window.SystemBackdrop = null;
            }
            else if (!DesktopAcrylicController.IsSupported())
            {
                // Remote sessions, some GPUs/policies: let the stock backdrop handle its own fallback.
                if (window.SystemBackdrop is not DesktopAcrylicBackdrop)
                {
                    LocalLog.Write("DesktopAcrylicController unsupported; using DesktopAcrylicBackdrop.");
                    window.SystemBackdrop = new DesktopAcrylicBackdrop();
                }
            }
            else if (window.SystemBackdrop is not GlassBackdrop)
            {
                window.SystemBackdrop = new GlassBackdrop(); // desktop acrylic, lighter frosting, always "active"
            }
        }
        catch
        {
            window.SystemBackdrop = null; // unsupported: the opaque path still works
        }
    }

    /// <summary>
    /// Outer minus client height. Windows 11 keeps an invisible resize frame even with the border
    /// and title bar turned off; sizing the outer rectangle to the content height alone clips the
    /// content by this much (the case card's cut-off "Controls" button).
    /// </summary>
    public static int FrameHeight(Microsoft.UI.Windowing.AppWindow appWindow)
        => Math.Max(0, appWindow.Size.Height - appWindow.ClientSize.Height);

    public static ElementTheme ToElementTheme(Core.Presentation.AppTheme theme) => theme switch
    {
        Core.Presentation.AppTheme.Light => ElementTheme.Light,
        Core.Presentation.AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
