using AppleDeviceHub.App.Companion;
using AppleDeviceHub.App.Design;
using AppleDeviceHub.App.Views;
using AppleDeviceHub.Core.Presentation;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Ordinary resizable settings window on the same clear desktop glass as the flyout
/// (GlassBackdrop), with the glass controls inside. Closing hides it so it reopens instantly.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly SettingsView _view;
    private bool _sized;

    public SettingsWindow(HubViewModel viewModel, HubSettings settings, AudioOutputViewModel? audio = null,
        PlaybackViewModel? playback = null, CompanionConnectionViewModel? connection = null, CompanionHost? companion = null,
        AppleDeviceHub.Core.Updates.UpdateViewModel? updates = null)
    {
        _view = new SettingsView { ViewModel = viewModel, Settings = settings, Audio = audio, Playback = playback, Connection = connection, Companion = companion, Updates = updates };
        Content = _view;
        Title = viewModel.IsDemo ? "Clasp Settings (Demo)" : "Clasp Settings";
        // One continuous surface: content runs under the caption buttons (the title stays in the
        // taskbar, Alt+Tab and UIA; the brand row in the sidebar replaces the drawn title).
        ExtendsContentIntoTitleBar = true;
        // The caption buttons otherwise follow the Windows theme, not the app's, so a Light app
        // on a dark Windows gets white minimize/maximize/close glyphs on a light surface.
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

        _view.SetWindowVisible(false); // nothing animates until the window is first shown
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            _view.SetWindowVisible(false);
            sender.Hide();
        };
    }

    /// <summary>About > Show welcome guide.</summary>
    public event EventHandler? WelcomeRequested
    {
        add => _view.WelcomeRequested += value;
        remove => _view.WelcomeRequested -= value;
    }

    public void SetTheme(ElementTheme theme)
    {
        _view.RequestedTheme = theme;
        ApplyCaptionButtonColors();
    }

    public void SetPlaybackViewModel(PlaybackViewModel? playback) => _view.Playback = playback;

    /// <summary>Scrolls to the battery history and refreshes it.</summary>
    public void ShowBatteryHistory() => _view.ShowBatteryHistory();

    /// <summary>Dev-only: jumps to a named section (--settings-section=).</summary>
    public void JumpToSection(string name) => _view.JumpToSectionByName(name);

    /// <summary>Scrolls back to the top and selects the first rail item.</summary>
    public void ResetToTop() => _view.ResetToTop();

    public void ShowAndFocus()
    {
        if (!_sized)
        {
            _sized = true;
            var (x, y) = NativeMethods.CursorPosition();
            var monitor = MonitorDetails.FromPoint(x, y);
            int w = (int)Math.Round(760 * monitor.Scale);
            int h = (int)Math.Round(700 * monitor.Scale);
            var work = monitor.WorkArea;
            w = Math.Min(w, work.Width);
            h = Math.Min(h, work.Height);
            AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        }
        EffectsPolicy.Refresh();
        _ = _view.Connection?.RefreshAsync();
        AppWindow.Show(true);
        _view.SetWindowVisible(true);
        Activate();
        // The lens rim and edge band ramp in as the window opens (skipped under reduced motion).
        // Queued behind the first layout, so the templates exist the first time the window is shown.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => GlassLens.Materialise(_view));
    }

    private void ApplyCaptionButtonColors()
    {
        try
        {
            var dark = _view.ActualTheme == ElementTheme.Dark;
            var bar = AppWindow.TitleBar;
            // A faint backing behind the glyphs, so they keep 3:1 over a pure white or black desktop now that the glass is
            // lighter. Not drawn when transparency is reduced or High Contrast is on (the window is opaque then).
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
        try
        {
            // The same clear desktop glass as the flyout (Mica only tints with the wallpaper and
            // shows nothing behind the window); the readable surfaces keep long text legible.
            WindowChrome.ApplyFloatingBackdrop(this);
        }
        catch
        {
            SystemBackdrop = null;
        }
    }
}
