using AppleDeviceHub.App.Design;
using AppleDeviceHub.App.Views;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// The tray-anchored control panel. One instance lives for the whole session: it is shown and
/// hidden, never closed (closing an acrylic WinUI window can crash natively; the upstream
/// NTPods client hit the same crash in its island popup).
/// </summary>
internal sealed class FlyoutWindow : Window
{
    private const double WidthDip = 380;
    private const double MarginDip = 12;

    private readonly FlyoutView _view;
    private readonly HubViewModel _viewModel;
    private readonly nint _hwnd;
    private TaskbarEdge _edge = TaskbarEdge.Bottom;
    private PixelRect? _iconRect;
    private bool _fromOverflow;
    private FlyoutOpenReason _pendingReason;
    // Open from the hidden-icons popup: the show is deferred (a few ms) until that popup has closed.
    private DispatcherQueueTimer? _overflowWaitTimer;
    private List<nint> _overflowHandles = new();
    private long _overflowWaitStartTicks;
    private long _pendingGeneration;
    private bool _pendingByKeyboard;
    private bool _pendingOpen;
    private (int X, int Y)? _cursor;
    private bool _isOpen;
    private bool _isClosing;
    private bool _prewarmed;
    private long _lastHiddenTicks;
    private long _generation;
    private bool _passive;
    private bool _pointerOver;
    private DispatcherQueueTimer? _passiveTimer;
    private int _passiveSeconds;
    // Foreground backstop: kept in a field so the delegate is not collected while hooked.
    private readonly NativeMethods.WinEventProc _foregroundProc;
    private nint _foregroundHook;
    private long _openedAtTicks;
    private bool _regrabFailedLogged;
    private const long ForegroundGraceMs = 600;
    // Click-outside backstop: a low-level mouse hook, installed only while the panel is open.
    private readonly NativeMethods.HookProc _mouseProc;
    private nint _mouseHook;

    public FlyoutWindow(HubViewModel viewModel, PlaybackViewModel? playbackViewModel = null,
        AudioOutputViewModel? audioOutput = null, CompanionConnectionViewModel? connection = null,
        AppleDeviceHub.Core.Updates.UpdateViewModel? updates = null,
        HubSettings? settings = null)
    {
        _viewModel = viewModel;
        _foregroundProc = OnForegroundChanged;
        _mouseProc = OnMouseHook;
        _view = new FlyoutView
        {
            ViewModel = viewModel,
            Playback = playbackViewModel,
            AudioOutput = audioOutput,
            Connection = connection,
            Updates = updates,
            Settings = settings,
            Width = WidthDip,
        };
        Content = _view;
        Title = "Clasp";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        WindowChrome.MakeFloating(this, activatable: true);
        WindowChrome.ApplyFloatingBackdrop(this);
        EffectsPolicy.Changed += (_, _) => WindowChrome.ApplyFloatingBackdrop(this);

        Activated += OnActivated;
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            _ = HideAsync();
        };

        _view.PointerEntered += (_, _) => _pointerOver = true;
        _view.PointerExited += (_, _) =>
        {
            _pointerOver = false;
            if (_passive && _isOpen && !_isClosing) StartPassiveTimer();
        };

        _view.DismissRequested += (_, _) => _ = HideAsync();
        _view.ContentHeightChanged += (_, _) => Refit();
        _view.SettingsRequested += async (_, _) =>
        {
            await HideAsync();
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        };
        _view.BatteryHistoryRequested += async (_, _) =>
        {
            await HideAsync();
            BatteryHistoryRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? SettingsRequested;

    /// <summary>The header's battery-history button was pressed (the flyout has already been hidden).</summary>
    public event EventHandler? BatteryHistoryRequested;

    /// <summary>Sets the "Last seen ..." line under the connection status; null or empty hides it.</summary>
    public void SetLastHeardText(string? text) => _view.SetLastHeardText(text);

    public bool IsOpen => _isOpen;

    /// <summary>True while the panel is showing as an unfocused case-open peek.</summary>
    public bool IsPassive => _passive && _isOpen && !_isClosing;

    public void SetAudioOutputViewModel(AudioOutputViewModel? audioOutput) => _view.AudioOutput = audioOutput;

    public void SetPlaybackViewModel(PlaybackViewModel? playbackViewModel) => _view.Playback = playbackViewModel;

    public void SetTheme(ElementTheme theme) => _view.RequestedTheme = theme;

    /// <summary>
    /// Realise the XAML tree once, invisibly and off-screen, so the first real open can measure
    /// its height before showing (no flash at a wrong size or position).
    /// </summary>
    public void Prewarm()
    {
        if (_prewarmed) return;
        _prewarmed = true;
        try
        {
            Motion.VisualOf(_view).Opacity = 0f;
            AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 380, 400));
            _view.Loaded += HideAfterPrewarm;
            AppWindow.Show(false);
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Flyout prewarm failed: {ex.Message}");
        }
    }

    private void HideAfterPrewarm(object sender, RoutedEventArgs e)
    {
        _view.Loaded -= HideAfterPrewarm;
        if (!_isOpen) AppWindow.Hide();
    }

    /// <summary>Tray click: open, or close if already open.</summary>
    public void Toggle(PixelRect? iconRect, (int X, int Y) cursor)
    {
        // A click on a passive peek promotes it to the normal focused panel instead of hiding it.
        if (IsPassive)
        {
            Open(iconRect, cursor, FlyoutOpenReason.TrayClick);
            return;
        }
        // A second click while we are still waiting for the hidden-icons popup cancels the open.
        if (_pendingOpen)
        {
            CancelPendingOpen();
            _lastHiddenTicks = Environment.TickCount64;
            return;
        }
        if (_isOpen)
        {
            _ = HideAsync();
            return;
        }
        // A click on the icon while open first deactivates (and hides) the flyout; without this
        // guard the same click would immediately reopen it.
        if (Environment.TickCount64 - _lastHiddenTicks < 350) return;
        Open(iconRect, cursor, FlyoutOpenReason.TrayClick);
    }

    /// <param name="iconRect">The tray icon's rectangle, if known.</param>
    /// <param name="cursor">The anchor point when the icon rectangle is unknown.</param>
    /// <param name="reason">Why it opens. Only <see cref="FlyoutOpenReason.TrayClick"/> looks at the hidden-icons popup.</param>
    /// <param name="byKeyboard">Opened by keyboard tray activation: focus shows its ring.</param>
    public void Open(PixelRect? iconRect, (int X, int Y)? cursor, FlyoutOpenReason reason, bool byKeyboard = false)
    {
        // Reopening during the exit animation: the pending hide must not win afterwards.
        var generation = ++_generation;
        _isClosing = false;
        CancelPendingOpen();
        try
        {
            _iconRect = iconRect;
            _cursor = cursor ?? (iconRect is null ? NativeMethods.CursorPosition() : null);
            var overflowHandles = new List<nint>();
            // Non-tray opens (startup, clasp:// commands, hotkeys, code) never look at the hidden-icons
            // popup: they show now, at the normal anchor, and can never be cancelled.
            _fromOverflow = FlyoutOpenPolicy.ChecksOverflow(reason)
                && DetectOverflowOpen(_iconRect, _cursor, out overflowHandles);

            // The hidden-icons popup is still on screen right after the click and would draw over the
            // flyout. Windows closes it on its own once our window takes the foreground, so wait (on
            // the UI thread, polling) until it is not visible, for at most OverflowCloseWait.MaxWaitMs.
            if (FlyoutOpenPolicy.ShouldWaitForOverflow(reason, _fromOverflow && overflowHandles.Any(IsPopupGenuinelyOpen)))
            {
                BeginOverflowWait(generation, byKeyboard, overflowHandles, reason);
                return;
            }
            ShowCore(generation, byKeyboard);
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Flyout open failed: {ex}");
        }
    }

    private void BeginOverflowWait(long generation, bool byKeyboard, List<nint> handles, FlyoutOpenReason reason)
    {
        _pendingReason = reason;
        _overflowHandles = handles;
        _pendingGeneration = generation;
        _pendingByKeyboard = byKeyboard;
        _pendingOpen = true;
        _overflowWaitStartTicks = Environment.TickCount64;
        _overflowWaitTimer ??= CreateOverflowWaitTimer();
        _overflowWaitTimer.Interval = TimeSpan.FromMilliseconds(OverflowCloseWait.PollIntervalMs);
        _overflowWaitTimer.Start();
    }

    private DispatcherQueueTimer CreateOverflowWaitTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (!_pendingOpen) { timer.Stop(); return; }
            long elapsed = Environment.TickCount64 - _overflowWaitStartTicks;
            bool visible = _overflowHandles.Any(IsPopupGenuinelyOpen);
            var action = FlyoutOpenPolicy.Decide(
                _pendingReason, elapsed, visible,
                escapePressed: NativeMethods.IsKeyDown(NativeMethods.VK_ESCAPE),
                clickedElsewhere: ClickedElsewhereDuringWait(),
                otherAppForeground: IsOtherAppForeground());
            if (action == OverflowWaitAction.Wait) return;
            timer.Stop();
            _pendingOpen = false;
            if (action == OverflowWaitAction.Cancel)
            {
                // The user moved on (Escape, a click elsewhere, or another app took focus): do not pop up late.
                LocalLog.Write($"Flyout open: cancelled after {elapsed} ms, the user moved on");
                _lastHiddenTicks = Environment.TickCount64;
                return;
            }
            LocalLog.Write(OverflowCloseWait.Describe(elapsed, visible));
            try
            {
                ShowCore(_pendingGeneration, _pendingByKeyboard);
            }
            catch (Exception ex)
            {
                LocalLog.Write($"Flyout open failed: {ex}");
            }
        };
        return timer;
    }

    /// <summary>True when the foreground window belongs to something other than the taskbar, the hidden-icons popup or us.</summary>
    private bool IsOtherAppForeground()
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == 0 || fg == _hwnd) return false;
            if (NativeMethods.GetWindowThreadProcessId(fg, out uint pid) != 0 && pid == (uint)Environment.ProcessId) return false;
            var root = NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOT);
            return !(IsTaskbarWindow(fg) || (root != 0 && IsTaskbarWindow(root)) || _overflowHandles.Contains(fg));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A mouse button is down over a window that is not the taskbar, the hidden-icons popup or ours.</summary>
    private static bool ClickedElsewhereDuringWait()
    {
        try
        {
            if (!NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON) && !NativeMethods.IsKeyDown(NativeMethods.VK_RBUTTON)) return false;
            var c = NativeMethods.CursorPosition();
            var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = c.X, Y = c.Y });
            if (hit == 0) return false;
            if (NativeMethods.GetWindowThreadProcessId(hit, out uint pid) != 0 && pid == (uint)Environment.ProcessId) return false;
            return !IsTaskbarWindow(NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Abandons a deferred open (user clicked again, or another open/hide took over).</summary>
    private void CancelPendingOpen()
    {
        if (!_pendingOpen) return;
        _pendingOpen = false;
        _overflowWaitTimer?.Stop();
    }

    private void ShowCore(long generation, bool byKeyboard)
    {
        EffectsPolicy.Refresh();
        _viewModel.Refresh();

        var monitor = MonitorFor(_iconRect, _cursor);

        // Put the (hidden) window on the target monitor first so WinUI adopts that monitor's
        // DPI before the final size is applied.
        AppWindow.Move(new PointInt32(monitor.WorkArea.X + 8, monitor.WorkArea.Y + 8));
        var placement = PlaceFor(monitor);
        LocalLog.Write($"Flyout open: anchor={placement.AnchorSource} edge={placement.Edge} dpi={monitor.Dpi} bounds={placement.Bounds}");

        bool wasPassive = _passive;
        if (wasPassive) EndPassive();
        AppWindow.Show(true);
        WindowChrome.RemoveNativeFrame(_hwnd);
        // The plain call fails under the foreground lock (e.g. opened from the taskbar overflow);
        // without activation the light-dismiss in OnActivated would never fire.
        if (!NativeMethods.ForceForeground(_hwnd)) LocalLog.Write("Flyout open: could not take the foreground.");
        _openedAtTicks = Environment.TickCount64;
        _regrabFailedLogged = false;
        InstallForegroundHook();
        InstallMouseHook();
        _isOpen = true;
        Motion.PlayEntrance(_view, _edge);
        _view.PlayEntranceGlint();
        _view.OnShown(generation, byKeyboard);
    }

    /// <summary>
    /// Case-open pop-up: the same panel, shown without taking focus, hidden again after
    /// <paramref name="seconds"/> (clamped 3-30) unless the pointer is over it. Clicking into it
    /// turns it into a normal panel.
    /// </summary>
    public void PresentPassive(PixelRect? iconRect, int seconds)
    {
        _passiveSeconds = Math.Clamp(seconds, 3, 30);
        CancelPendingOpen();
        if (_isOpen && !_isClosing)
        {
            _view.PlayCaseOpen();
            if (_passive) StartPassiveTimer();
            return;
        }

        var generation = ++_generation;
        _isClosing = false;
        try
        {
            EffectsPolicy.Refresh();
            _viewModel.Refresh();

            _iconRect = iconRect;
            _cursor = iconRect is null ? NativeMethods.CursorPosition() : null;
            _fromOverflow = false; // a passive peek never reacts to the hidden-icons popup
            var monitor = MonitorFor(_iconRect, _cursor);

            AppWindow.Move(new PointInt32(monitor.WorkArea.X + 8, monitor.WorkArea.Y + 8));
            var placement = PlaceFor(monitor);
            LocalLog.Write($"Flyout passive open: anchor={placement.AnchorSource} edge={placement.Edge} dpi={monitor.Dpi} bounds={placement.Bounds}");

            _passive = true;
            _pointerOver = false;
            AppWindow.Show(false);
            WindowChrome.RemoveNativeFrame(_hwnd);
            _openedAtTicks = Environment.TickCount64;
            InstallMouseHook();
            _isOpen = true;
            Motion.PlayEntrance(_view, _edge);
            _view.PlayEntranceGlint();
            _view.OnShown(generation, byKeyboard: false, takeFocus: false);
            _view.PlayCaseOpen();
            StartPassiveTimer();
        }
        catch (Exception ex)
        {
            _passive = false;
            RemoveMouseHook();
            LocalLog.Write($"Flyout passive open failed: {ex}");
        }
    }

    private void StartPassiveTimer()
    {
        _passiveTimer ??= CreatePassiveTimer();
        _passiveTimer.Stop();
        _passiveTimer.Interval = TimeSpan.FromSeconds(_passiveSeconds);
        _passiveTimer.Start();
    }

    private DispatcherQueueTimer CreatePassiveTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_passive || !_isOpen || _isClosing) return;
            if (_pointerOver) return; // restarted by PointerExited
            LocalLog.Write("Flyout passive timeout.");
            _ = HideAsync();
        };
        return timer;
    }

    /// <summary>Leaves passive mode (user interaction or a normal Open): no more auto-hide.</summary>
    private void EndPassive()
    {
        _passive = false;
        _passiveTimer?.Stop();
    }

    private void InstallForegroundHook()
    {
        if (_foregroundHook != 0) return;
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_foregroundHook == 0) LocalLog.Write("Flyout foreground hook could not be installed.");
    }

    private void RemoveForegroundHook()
    {
        if (_foregroundHook == 0) return;
        NativeMethods.UnhookWinEvent(_foregroundHook);
        _foregroundHook = 0;
    }

    /// <summary>Backstop for light dismissal when the window never got activated: another process took the foreground.</summary>
    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (!_isOpen || _isClosing || _passive || hwnd == 0 || hwnd == _hwnd) return;
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0 || pid == (uint)Environment.ProcessId) return;
        if (Environment.TickCount64 - _openedAtTicks < ForegroundGraceMs)
        {
            // The taskbar overflow closes right after we open and hands focus elsewhere: take it back.
            if (IsTaskbarWindow(hwnd)) return;
            if (!NativeMethods.ForceForeground(_hwnd) && !_regrabFailedLogged)
            {
                _regrabFailedLogged = true;
                LocalLog.Write("Flyout: could not retake the foreground after opening.");
            }
            return;
        }
        LocalLog.Write("Flyout dismissed: another app took the foreground.");
        _ = HideAsync();
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != 0) return;
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc,
            NativeMethods.GetModuleHandleW(null), 0);
        if (_mouseHook == 0) LocalLog.Write("Flyout mouse hook could not be installed.");
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == 0) return;
        NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
    }

    /// <summary>
    /// Click-outside backstop: a button press outside the panel hides it even when the panel never
    /// became the foreground window (so no deactivation or foreground change will ever follow).
    /// Runs inside the input pipeline, so it only classifies the click and defers the hide.
    /// </summary>
    private nint OnMouseHook(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && _isOpen && !_isClosing && IsButtonDown((uint)wParam))
            {
                // MSLLHOOKSTRUCT starts with the cursor position, in physical pixels.
                int x = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 0);
                int y = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);
                if (IsOutsideClickToDismiss(x, y))
                    DispatcherQueue.TryEnqueue(() => _ = HideAsync());
            }
        }
        catch
        {
            // A hook callback must never throw.
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsButtonDown(uint msg) => msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
        or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_NCLBUTTONDOWN or NativeMethods.WM_NCRBUTTONDOWN
        or NativeMethods.WM_NCMBUTTONDOWN;

    private bool IsOutsideClickToDismiss(int x, int y)
    {
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return false;
        if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) return false;

        // Our tray icon: its own handler toggles (or promotes a passive peek); hiding here would
        // make the same click close and then reopen the panel.
        if (_iconRect is { } icon && x >= icon.X && x < icon.X + icon.Width && y >= icon.Y && y < icon.Y + icon.Height)
            return false;

        var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (hit != 0)
        {
            // Our own popups (dropdowns, menus, tooltips) and windows are not "outside".
            if (NativeMethods.GetWindowThreadProcessId(hit, out uint pid) != 0 && pid == (uint)Environment.ProcessId) return false;
            // Right after opening, the taskbar / overflow popup is still settling (the open click came from there).
            if (Environment.TickCount64 - _openedAtTicks < ForegroundGraceMs
                && IsTaskbarWindow(NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT))) return false;
        }
        return true;
    }

    private static bool IsTaskbarWindow(nint hwnd)
    {
        var name = new char[64];
        int length = GetClassName(hwnd, name, name.Length);
        if (length == 0) return false;
        return new string(name, 0, length) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, [System.Runtime.InteropServices.Out] char[] lpClassName, int nMaxCount);

    private static readonly string[] OverflowClasses = { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" };

    /// <summary>
    /// True when this tray click came from the hidden-icons popup, which Windows closes right after the click.
    /// Primary test: the icon rectangle lies inside an overflow window that is genuinely open
    /// (<see cref="IsPopupGenuinelyOpen"/>). Backup, for when the icon rectangle is unknown: the foreground
    /// window, or the window under the cursor, is a genuinely open overflow window.
    /// </summary>
    private static bool DetectOverflowOpen(PixelRect? iconRect, (int X, int Y)? cursor, out List<nint> handles)
    {
        // The overflow windows to watch for closing: the ones that hold our icon, or were clicked.
        handles = new List<nint>();
        try
        {
            foreach (var cls in OverflowClasses)
            {
                nint h = 0;
                while ((h = FindWindowEx(0, h, cls, null)) != 0)
                {
                    if (IsPopupGenuinelyOpen(h) && NativeMethods.GetWindowRect(h, out var r)
                        && FlyoutPlacement.IsIconInOverflow(iconRect, new[] { r.ToPixelRect() }))
                        handles.Add(h);
                }
            }
            if (handles.Count > 0) return true;

            var foreground = NativeMethods.GetForegroundWindow();
            if (IsOverflowWindow(foreground) && IsPopupGenuinelyOpen(foreground)) { handles.Add(foreground); return true; }
            if (cursor is { } c)
            {
                var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = c.X, Y = c.Y });
                var root = hit == 0 ? 0 : NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
                if (IsOverflowWindow(root) && IsPopupGenuinelyOpen(root)) { handles.Add(root); return true; }
            }
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Overflow detection failed: {ex.Message}");
        }
        return false;
    }

    private const int DWMWA_CLOAKED = 14;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// IsWindowVisible alone is not trustworthy: on Windows 11 the overflow XAML island window keeps the
    /// WS_VISIBLE style while it is cloaked (hidden by DWM) or parked with an empty / off-screen rectangle.
    /// Genuinely open means visible AND not cloaked (DWMWA_CLOAKED == 0) AND a non-empty rectangle that
    /// overlaps the virtual screen.
    /// </summary>
    private static bool IsPopupGenuinelyOpen(nint hwnd)
    {
        try
        {
            bool visible = IsWindowVisible(hwnd);
            if (!visible) return false;
            bool isCloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
            bool haveRect = NativeMethods.GetWindowRect(hwnd, out var r);
            bool nonEmpty = haveRect && r.Right > r.Left && r.Bottom > r.Top;
            int vx = NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN), vy = NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vr = vx + NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN), vb = vy + NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN);
            bool onScreen = haveRect && r.Right > vx && r.Left < vr && r.Bottom > vy && r.Top < vb;
            return FlyoutOpenPolicy.IsPopupGenuinelyOpen(visible, isCloaked, nonEmpty, onScreen);
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    private static bool IsOverflowWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        var name = new char[64];
        int length = GetClassName(hwnd, name, name.Length);
        return length != 0 && Array.IndexOf(OverflowClasses, new string(name, 0, length)) >= 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    private static MonitorDetails MonitorFor(PixelRect? iconRect, (int X, int Y)? cursor)
    {
        var anchor = iconRect is { } r ? (r.CenterX, r.CenterY) : cursor ?? NativeMethods.CursorPosition();
        return MonitorDetails.FromPoint(anchor.Item1, anchor.Item2);
    }

    /// <summary>Measure the content at the flyout width and place the window against its anchor.</summary>
    private PlacementResult PlaceFor(MonitorDetails monitor)
    {
        double scale = monitor.Scale;
        _view.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        int width = (int)Math.Ceiling(WidthDip * scale);
        // AppWindow sizes the outer frame; borderless windows keep an invisible frame, so add it.
        int height = (int)Math.Ceiling(Math.Max(_view.DesiredSize.Height, 200) * scale) + WindowChrome.FrameHeight(AppWindow);

        int margin = (int)Math.Round(MarginDip * scale);
        // Only matters when the taskbar auto-hides (work area equals the monitor); otherwise the work area decides.
        var taskbarEdge = NativeMethods.QueryTaskbarEdge();
        var placement = _fromOverflow
            ? FlyoutPlacement.PlaceAtTaskbarCorner(monitor.Bounds, monitor.WorkArea, width, height, margin, taskbarEdge)
            : FlyoutPlacement.Place(_iconRect, _cursor, monitor.Bounds, monitor.WorkArea, width, height, margin, taskbarEdge);
        _edge = placement.Edge;
        var b = placement.Bounds;
        AppWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        return placement;
    }

    /// <summary>Content grew or shrank while open: keep the window hugging it, still anchored to the tray.</summary>
    private void Refit()
    {
        if (!_isOpen || _isClosing) return;
        try
        {
            PlaceFor(MonitorFor(_iconRect, _cursor));
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Flyout refit failed: {ex.Message}");
        }
    }

    public async Task HideAsync()
    {
        CancelPendingOpen();
        if (!_isOpen || _isClosing) return;
        _isClosing = true;
        EndPassive();
        RemoveForegroundHook();
        RemoveMouseHook();
        long generation = _generation;
        _view.OnHidden(generation);
        try
        {
            await Motion.PlayExitAsync(_view, _edge);
            if (generation != _generation) return; // reopened meanwhile: keep it visible
            AppWindow.Hide();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Flyout hide failed: {ex.Message}");
        }
        finally
        {
            if (generation == _generation)
            {
                _isOpen = false;
                _isClosing = false;
                _lastHiddenTicks = Environment.TickCount64;
            }
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // WinUI can restore native presenter frame bits while showing the window.
        WindowChrome.RemoveNativeFrame(_hwnd);

        // Clicked into the passive pop-up: from now on it is a normal, light-dismissed panel.
        if (_passive && _isOpen && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            EndPassive();
            _openedAtTicks = Environment.TickCount64;
            _regrabFailedLogged = false;
            InstallForegroundHook();
            InstallMouseHook();
            LocalLog.Write("Flyout passive -> active.");
            return;
        }

        // Light dismissal: clicking anywhere else closes the panel.
        if (_isOpen && args.WindowActivationState == WindowActivationState.Deactivated) _ = HideAsync();
    }
}
