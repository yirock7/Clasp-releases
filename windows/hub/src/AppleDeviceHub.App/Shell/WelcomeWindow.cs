using AppleDeviceHub.App.Companion;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.App.Views;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// The welcome guide in its own small window on the same clear glass as Settings (GlassBackdrop, content under the
/// caption buttons). Not resizable. Done, Skip, Esc and the close button all end the guide: the window hides (it is
/// reused) and <see cref="HubSettings.WelcomeCompleted"/> is set, except on the demo transport, which never saves it.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private const double WidthDip = 480;
    private const double HeightDip = 640;

    private readonly WelcomeView _view;
    private readonly HubSettings _settings;
    private readonly bool _isDemo;
    private bool _placed;
    private bool _isOpen;

    public WelcomeWindow(HubViewModel viewModel, HubSettings settings, CompanionConnectionViewModel? connection, CompanionHost? companion)
    {
        _settings = settings;
        _isDemo = viewModel.IsDemo;
        _view = new WelcomeView();
        _view.Setup(viewModel, settings, connection, companion);
        _view.Finished += (_, completed) => End();
        Content = _view;
        Title = viewModel.IsDemo ? "Welcome to Clasp (Demo)" : "Welcome to Clasp";

        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        _view.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
        ApplyCaptionButtonColors();
        ApplyBackdrop();
        EffectsPolicy.Changed += (_, _) => { ApplyBackdrop(); ApplyCaptionButtonColors(); };

        try
        {
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch
        {
            // Cosmetic only.
        }

        // The X button counts as Skip. The window is kept and hidden, like Settings.
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            End();
        };
    }

    public void SetTheme(ElementTheme theme)
    {
        _view.RequestedTheme = theme;
        ApplyCaptionButtonColors();
    }

    /// <summary>Opens the guide on <paramref name="page"/> (0 to 2) and brings it to the front.</summary>
    public void ShowAndFocus(int page = 0)
    {
        _view.Restart(page);
        if (!_placed)
        {
            _placed = true;
            var (x, y) = NativeMethods.CursorPosition();
            var monitor = MonitorDetails.FromPoint(x, y);
            int w = (int)Math.Round(WidthDip * monitor.Scale);
            int h = (int)Math.Round(HeightDip * monitor.Scale);
            var work = monitor.WorkArea;
            w = Math.Min(w, work.Width);
            h = Math.Min(h, work.Height);
            AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        }
        EffectsPolicy.Refresh();
        _isOpen = true;
        AppWindow.Show(true);
        Activate();
        // Queued behind the first layout, so the templates exist the first time the window is shown.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            GlassLens.Materialise(_view);
            _view.FocusPrimary();
        });
    }

    private void End()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _view.Detach();
        // Demo runs (screenshots, the startup smoke) must never write the settings file.
        if (!_isDemo) _settings.WelcomeCompleted = true;
        AppWindow.Hide();
    }

    private void ApplyCaptionButtonColors()
    {
        try
        {
            var dark = _view.ActualTheme == ElementTheme.Dark;
            var bar = AppWindow.TitleBar;
            var backing = EffectsPolicy.ReduceTransparency ? Colors.Transparent
                : dark ? ColorHelper.FromArgb(0x4D, 0x0C, 0x0E, 0x13) : ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
            bar.ButtonBackgroundColor = backing;
            bar.ButtonInactiveBackgroundColor = backing;
            bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
            bar.ButtonInactiveForegroundColor = dark
                ? ColorHelper.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
                : ColorHelper.FromArgb(0xFF, 0x70, 0x70, 0x70);
            bar.ButtonHoverBackgroundColor = dark
                ? ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);
            bar.ButtonPressedBackgroundColor = dark
                ? ColorHelper.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0x0C, 0x00, 0x00, 0x00);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private void ApplyBackdrop()
    {
        try { WindowChrome.ApplyFloatingBackdrop(this); }
        catch { SystemBackdrop = null; }
    }
}
