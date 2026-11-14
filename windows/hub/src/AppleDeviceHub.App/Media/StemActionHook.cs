using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.App.Media;

/// <summary>
/// Optional remapping of AirPods stem presses, which reach Windows as media keys. Installs a
/// low-level keyboard hook (WH_KEYBOARD_LL) on its own thread with its own message loop, so the
/// callback never competes with the UI thread. The callback only asks <see cref="StemActionPolicy"/>
/// and returns; actions run on the thread pool. Synthetic keys (including our own) are never touched,
/// which also prevents feedback loops. Windows cannot tell a stem press from a keyboard media key,
/// so while a remap is active the keyboard keys are remapped too.
/// <para>
/// Create it only when the setting is turned on and dispose it when it is turned off; nothing is
/// hooked while it does not exist. Whether a low-level hook sees AVRCP presses at all depends on the
/// Windows Bluetooth stack: <see cref="MediaKeysSeen"/> lets the UI say so honestly.
/// </para>
/// </summary>
internal sealed class StemActionHook : IDisposable
{
    private readonly StemActionPolicy _policy;
    private readonly Func<StemContext> _context;
    private readonly Action<string>? _log;
    private readonly StemActionInterop.LowLevelKeyboardProc _proc; // field keeps the delegate alive for the native hook
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private nint _hook;
    private int _mediaKeysSeen;
    private int _disposed;

    /// <param name="policy">Decision logic; configure it with the user's mappings.</param>
    /// <param name="context">Cheap, non-blocking read of "AirPods connected / output is AirPods"; called on the hook thread.</param>
    /// <param name="log">Optional diagnostics; must not block.</param>
    public StemActionHook(StemActionPolicy policy, Func<StemContext> context, Action<string>? log = null)
    {
        _policy = policy;
        _context = context;
        _log = log;
        _proc = HookCallback;
    }

    /// <summary>How many media-key events the hook has observed (non-injected). Zero after real stem presses means Windows delivers them another way.</summary>
    public int MediaKeysSeen => Volatile.Read(ref _mediaKeysSeen);

    /// <summary>True once the hook is installed.</summary>
    public bool IsRunning => Volatile.Read(ref _hook) != 0;

    /// <summary>Installs the hook. Returns false when Windows refused it; the feature then simply does nothing.</summary>
    public bool Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _thread is not null) return IsRunning;
        _thread = new Thread(Run) { IsBackground = true, Name = "Clasp stem action hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
        return IsRunning;
    }

    private void Run()
    {
        _threadId = StemActionInterop.GetCurrentThreadId();
        // Touch the queue so PostThreadMessage from Dispose can reach this thread.
        StemActionInterop.PeekMessageW(out _, 0, 0, 0, StemActionInterop.PM_NOREMOVE);
        var hook = StemActionInterop.SetWindowsHookExW(
            StemActionInterop.WH_KEYBOARD_LL, _proc, StemActionInterop.GetModuleHandleW(null), 0);
        if (hook == 0)
        {
            _log?.Invoke($"Stem actions: keyboard hook refused (error {Marshal.GetLastWin32Error()}).");
            _ready.Set();
            return;
        }
        Volatile.Write(ref _hook, hook);
        _ready.Set();
        try
        {
            while (StemActionInterop.GetMessageW(out _, 0, 0, 0) > 0)
            {
                // The hook callback runs inside GetMessage; nothing else to dispatch here.
            }
        }
        finally
        {
            StemActionInterop.UnhookWindowsHookEx(hook);
            Volatile.Write(ref _hook, 0);
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code == StemActionInterop.HC_ACTION && lParam != 0)
            {
                var data = Marshal.PtrToStructure<StemActionInterop.KbdLlHookStruct>(lParam);
                if (TryMap(data.VkCode, out var gesture)
                    && (data.Flags & (StemActionInterop.LLKHF_INJECTED | StemActionInterop.LLKHF_LOWER_IL_INJECTED)) == 0)
                {
                    Interlocked.Increment(ref _mediaKeysSeen);
                    var message = (uint)wParam;
                    if (message is StemActionInterop.WM_KEYDOWN or StemActionInterop.WM_SYSKEYDOWN)
                    {
                        var decision = _policy.OnKeyDown(gesture, _context());
                        if (decision.Swallow)
                        {
                            if (decision.ShouldRun) Dispatch(decision.Action);
                            return 1;
                        }
                    }
                    else if (message is StemActionInterop.WM_KEYUP or StemActionInterop.WM_SYSKEYUP
                        && _policy.OnKeyUp(gesture))
                    {
                        return 1;
                    }
                }
            }
        }
        catch
        {
            // A hook must never throw into Windows; on any doubt, let the key through.
        }
        return StemActionInterop.CallNextHookEx(0, code, wParam, lParam);
    }

    private static bool TryMap(uint vk, out StemGesture gesture)
    {
        switch (vk)
        {
            case StemActionInterop.VK_MEDIA_PLAY_PAUSE: gesture = StemGesture.PlayPause; return true;
            case StemActionInterop.VK_MEDIA_NEXT_TRACK: gesture = StemGesture.Next; return true;
            case StemActionInterop.VK_MEDIA_PREV_TRACK: gesture = StemGesture.Previous; return true;
            default: gesture = default; return false;
        }
    }

    /// <summary>Runs the action off the hook thread so the callback returns immediately.</summary>
    private void Dispatch(StemAction action)
    {
        _ = Task.Run(() =>
        {
            try
            {
                switch (action)
                {
                    case StemAction.MuteMicrophone:
                        var muted = StemActionInterop.ToggleDefaultMicrophoneMute();
                        _log?.Invoke(muted is null
                            ? "Stem actions: no microphone to mute."
                            : muted.Value ? "Stem actions: microphone muted." : "Stem actions: microphone unmuted.");
                        break;
                    case StemAction.PlayPause: SendKey(StemActionInterop.VK_MEDIA_PLAY_PAUSE); break;
                    case StemAction.Next: SendKey(StemActionInterop.VK_MEDIA_NEXT_TRACK); break;
                    case StemAction.Previous: SendKey(StemActionInterop.VK_MEDIA_PREV_TRACK); break;
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or InvalidCastException)
            {
                _log?.Invoke($"Stem actions: action failed ({exception.GetType().Name}).");
            }
        });
    }

    /// <summary>Sends a synthetic key press. Windows flags it as injected, so our own hook ignores it.</summary>
    private static void SendKey(ushort vk)
    {
        var down = new StemActionInterop.Input
        {
            Type = StemActionInterop.INPUT_KEYBOARD,
            VirtualKey = vk,
            Flags = StemActionInterop.KEYEVENTF_EXTENDEDKEY,
        };
        var up = down with { Flags = StemActionInterop.KEYEVENTF_EXTENDEDKEY | StemActionInterop.KEYEVENTF_KEYUP };
        var sent = StemActionInterop.SendInput(2, [down, up], Marshal.SizeOf<StemActionInterop.Input>());
        if (sent != 2) throw new InvalidOperationException("SendInput was blocked.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var thread = _thread;
        if (thread is not null)
        {
            if (_threadId != 0) StemActionInterop.PostThreadMessageW(_threadId, StemActionInterop.WM_QUIT, 0, 0);
            thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }
}
