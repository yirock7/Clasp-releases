using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Commands;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.App.Shell;

/// <summary>Result of trying to register one hotkey.</summary>
internal sealed record HotkeyRegistrationResult(ClaspCommand Command, HotkeyBinding Binding, bool Registered, string? Problem);

/// <summary>
/// Global hotkeys through RegisterHotKey on a hidden message-only window of its own (it does not
/// share the tray window, so no shared file changes). All commands are unassigned unless
/// <see cref="Apply"/> is given a binding. Must be created and used on the UI thread: WM_HOTKEY is
/// delivered to the thread that registered, which is the thread running the WinUI message loop.
/// A combination another program already owns is reported per binding
/// (ERROR_HOTKEY_ALREADY_REGISTERED), never fatal and never retried in a loop.
/// </summary>
internal sealed class Hotkeys : IDisposable
{
    private const string WindowClass = "Clasp.HotkeyWindow";
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private static readonly nint HWND_MESSAGE = -3;

    private readonly NativeMethods.WndProc _wndProc; // kept alive: the native window calls it
    private readonly nint _hwnd;
    private readonly Dictionary<int, ClaspCommand> _byId = new();
    private int _nextId = 0x4300;
    private bool _disposed;

    public Hotkeys()
    {
        _wndProc = WindowProc;
        var hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = WindowClass,
        };
        NativeMethods.RegisterClassExW(ref wc); // fails harmlessly if already registered in this process
        _hwnd = NativeMethods.CreateWindowExW(0, WindowClass, "Clasp hotkeys", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, hInstance, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"Hotkey window creation failed ({Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    public event EventHandler<ClaspCommand>? Pressed;

    /// <summary>
    /// Replaces every registration with <paramref name="bindings"/> (null or missing entries mean
    /// unassigned). Invalid bindings and two commands sharing one combination are reported, not
    /// registered. Safe to call again whenever a setting changes.
    /// </summary>
    public IReadOnlyList<HotkeyRegistrationResult> Apply(IReadOnlyDictionary<ClaspCommand, HotkeyBinding?> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnregisterAll();

        var results = new List<HotkeyRegistrationResult>();
        var taken = new HashSet<HotkeyBinding>();
        foreach (var command in Enum.GetValues<ClaspCommand>())
        {
            if (!bindings.TryGetValue(command, out var binding) || binding is null) continue;

            var validation = binding.Validate();
            if (!validation.IsValid) { results.Add(new(command, binding, false, validation.Reason)); continue; }
            if (!taken.Add(binding)) { results.Add(new(command, binding, false, Strings.Hotkey_Problem_AlreadyAssigned)); continue; }

            var id = _nextId++;
            if (HotkeyNative.RegisterHotKey(_hwnd, id, (uint)binding.Modifiers | MOD_NOREPEAT, binding.VirtualKey))
            {
                _byId[id] = command;
                results.Add(new(command, binding, true, null));
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                var problem = error == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? Strings.Hotkey_Problem_UsedByOtherProgram
                    : string.Format(CultureInfo.CurrentCulture, Strings.Hotkey_Problem_WindowsRefused, new Win32Exception(error).Message);
                results.Add(new(command, binding, false, problem));
                LocalLog.Write($"Hotkey for '{ClaspCommandText.CanonicalName(command)}' not registered (error {error}).");
            }
        }
        return results;
    }

    private void UnregisterAll()
    {
        foreach (var id in _byId.Keys) HotkeyNative.UnregisterHotKey(_hwnd, id);
        _byId.Clear();
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && _byId.TryGetValue((int)wParam, out var command))
        {
            try { Pressed?.Invoke(this, command); }
            catch (Exception ex) { LocalLog.Write($"Hotkey handler failed ({ex.GetType().Name})."); }
            return 0;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        UnregisterAll();
        _disposed = true;
        NativeMethods.DestroyWindow(_hwnd);
    }
}

/// <summary>Win32 declarations for hotkeys only (kept out of the shared NativeMethods class).</summary>
internal static class HotkeyNative
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);
}
