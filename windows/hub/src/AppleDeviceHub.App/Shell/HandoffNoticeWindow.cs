using AppleDeviceHub.App.Design;
using AppleDeviceHub.App.Views;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Small non-activating notice ("Your AirPods moved to another device") with Take back and
/// Dismiss, following the case card's window pattern: it never takes focus, rises from the
/// bottom of the active monitor, and is reused, never closed. It stays up until dismissed or a
/// generous timeout passes (longer while the pointer is over it or a Take back is running).
/// </summary>
internal sealed class HandoffNoticeWindow : Window
{
    private const double WidthDip = 340;
    private const double BottomMarginDip = 24;
    private static readonly TimeSpan AutoHide = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectedHide = TimeSpan.FromSeconds(4);

    private readonly HandoffNoticeView _view;
    private readonly CompanionConnectionViewModel _connection;
    private readonly DispatcherQueueTimer _hideTimer;
    private bool _isOpen;
    private bool _isClosing;
    private bool _pointerOver;
    private bool _loaded;
    private bool _pendingPresent;
    private int _generation;
    private MonitorDetails? _monitor;

    public HandoffNoticeWindow(CompanionConnectionViewModel connection)
    {
        _connection = connection;
        _view = new HandoffNoticeView { Connection = connection, Width = WidthDip };
        Content = _view;
        Title = "Clasp hand-off notice";

        WindowChrome.MakeFloating(this, activatable: false);
        WindowChrome.ApplyFloatingBackdrop(this);
        EffectsPolicy.Changed += (_, _) => WindowChrome.ApplyFloatingBackdrop(this);

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            if (!_pointerOver && !_connection.IsConnecting) _ = HideAsync();
        };

        _view.PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            _hideTimer.Stop();
        };
        _view.PointerExited += (_, _) =>
        {
            _pointerOver = false;
            RestartTimer(AutoHide);
        };
        _view.DismissRequested += (_, _) => _ = HideAsync();
        _view.ContentHeightChanged += (_, _) => Refit();
        _view.ConnectedAgain += (_, _) => RestartTimer(ConnectedHide);
        _view.Loaded += (_, _) =>
        {
            _loaded = true;
            if (_pendingPresent)
            {
                _pendingPresent = false;
                PlaceAndAnimate();
            }
        };
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            _ = HideAsync();
        };
    }

    public void SetTheme(ElementTheme theme) => _view.RequestedTheme = theme;

    /// <summary>Show the notice once for a hand-off episode; a repeat call while open only restarts the timer.</summary>
    public void Present()
    {
        try
        {
            EffectsPolicy.Refresh();
            if (_isOpen && !_isClosing)
            {
                RestartTimer(AutoHide);
                return;
            }

            _generation++;
            _isClosing = false;
            _view.Reset();

            if (!_loaded)
            {
                _pendingPresent = true;
                Motion.VisualOf(_view).Opacity = 0f;
                AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 340, 220));
                AppWindow.Show(false);
                return;
            }
            PlaceAndAnimate();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Hand-off notice failed: {ex}");
        }
    }

    private void PlaceAndAnimate()
    {
        var monitor = MonitorDetails.ForForegroundWindow();
        _monitor = monitor;
        AppWindow.Move(new PointInt32(monitor.WorkArea.X + 8, monitor.WorkArea.Y + 8));
        Fit(monitor);
        AppWindow.Show(false); // no activation, no focus steal
        _isOpen = true;
        Motion.PlayEntrance(_view, TaskbarEdge.Bottom);
        GlassLens.Materialise(_view);
        RestartTimer(AutoHide);
    }

    private void Fit(MonitorDetails monitor)
    {
        double scale = monitor.Scale;
        _view.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        int width = (int)Math.Ceiling(WidthDip * scale);
        int height = (int)Math.Ceiling(Math.Max(_view.DesiredSize.Height, 120) * scale) + WindowChrome.FrameHeight(AppWindow);
        var work = monitor.WorkArea;
        height = Math.Min(height, work.Height);
        int x = work.X + (work.Width - width) / 2;
        int y = work.Bottom - height - (int)Math.Round(BottomMarginDip * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void Refit()
    {
        if (!_isOpen || _isClosing || _monitor is not { } monitor) return;
        try { Fit(monitor); }
        catch (Exception ex) { LocalLog.Write($"Hand-off notice refit failed: {ex.Message}"); }
    }

    public async Task HideAsync()
    {
        if (!_isOpen || _isClosing) return;
        _isClosing = true;
        _hideTimer.Stop();
        int generation = _generation;
        try
        {
            await Motion.PlayExitAsync(_view, TaskbarEdge.Bottom);
            if (generation != _generation) return;
            AppWindow.Hide();
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Hand-off notice hide failed: {ex.Message}");
        }
        finally
        {
            if (generation == _generation)
            {
                _isOpen = false;
                _isClosing = false;
            }
        }
    }

    private void RestartTimer(TimeSpan after)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = after;
        if (!_pointerOver) _hideTimer.Start();
    }
}
