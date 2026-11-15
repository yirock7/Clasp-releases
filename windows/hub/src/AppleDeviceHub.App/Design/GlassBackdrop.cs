using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.UI;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Desktop acrylic for the floating windows (flyout, case card), with two differences from the
/// stock <see cref="DesktopAcrylicBackdrop"/>:
/// <list type="bullet">
/// <item>Explicit, lighter frosting per theme, so the desktop behind shows through the glass. In a
/// packet 2.2 A/B capture of the case card over the same wallpaper, the stock backdrop showed no
/// colour from behind and this one did (relative result only; see docs/packet-2.2/RESULTS.md).</item>
/// <item><c>IsInputActive</c> is always true. The case card never takes focus
/// (<c>WS_EX_NOACTIVATE</c>), and the stock backdrop then shows its flat "inactive" colour
/// instead of blur (microsoft-ui-xaml #10570).</item>
/// </list>
/// Reduced transparency and high contrast never get here: <see cref="Shell.WindowChrome"/>
/// removes the backdrop and the views switch to their opaque fills.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The controller is created in OnTargetConnected and disposed in OnTargetDisconnected, the backdrop's own lifetime hooks; SystemBackdrop is not IDisposable.")]
public sealed partial class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _defaultConfiguration;
    private SystemBackdropConfiguration? _configuration;
    private ICompositionSupportsSystemBackdrop? _connectedTarget;
    private XamlRoot? _connectedRoot;
    private bool _connected;
    private bool _targetAdded;
    private bool _usesFallbackConfiguration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        // This initializes the default configuration that GetDefault... returns for this
        // connected target. WinUI keeps that object current as the target's theme/accessibility
        // state changes, so callbacks can use the cached object without resolving the target again.
        base.OnTargetConnected(connectedTarget, xamlRoot);

        if (_connectedTarget is not null)
            throw new InvalidOperationException("A GlassBackdrop instance can serve only one target at a time.");

        _connectedTarget = connectedTarget;
        _connectedRoot = xamlRoot;
        _connected = true;

        SystemBackdropConfiguration defaults;
        try
        {
            defaults = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        }
        catch (ArgumentException ex)
        {
            // A bounded recovery for the native target-validation failure: keep this
            // connected window's acrylic controller, but seed its configuration from the
            // current XAML root. Later callbacks refresh this local configuration from the
            // cached root instead of repeating the failing native lookup.
            _usesFallbackConfiguration = true;
            defaults = CreateFallbackConfiguration(xamlRoot);
            Shell.LocalLog.Write($"GlassBackdrop: default configuration lookup failed; using the connected root theme ({AppleDeviceHub.Core.Presentation.LogText.ExceptionKind(ex)}).");
        }

        // Keep WinUI's managed configuration as the source of theme/accessibility changes.
        // The controller gets its own copy so we can preserve the case card's always-active
        // acrylic even when the owning window is inactive.
        _defaultConfiguration = defaults;
        var controllerConfiguration = CreateControllerConfiguration(defaults);
        _configuration = controllerConfiguration;
        var controller = _controller = new DesktopAcrylicController();
        controller.SetSystemBackdropConfiguration(controllerConfiguration);
        if (!controller.AddSystemBackdropTarget(connectedTarget))
        {
            Shell.LocalLog.Write("GlassBackdrop: AddSystemBackdropTarget failed; the window has no desktop acrylic.");
            DisposeController();
            return;
        }

        _targetAdded = true;
        ApplyAppearance(defaults.Theme);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            if (IsSameTarget(_connectedTarget, disconnectedTarget))
            {
                // Mark the connection inactive before teardown. A queued theme callback can
                // then do no work against a target whose controller is being removed.
                _connected = false;
                try
                {
                    DisposeController();
                }
                finally
                {
                    _configuration = null;
                    _defaultConfiguration = null;
                    _connectedRoot = null;
                    _connectedTarget = null;
                    _usesFallbackConfiguration = false;
                }
            }
        }
        finally
        {
            // Always release the framework's usage context, including when native cleanup fails.
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }

    // Called when the target's theme or high-contrast state changes. The cached configuration
    // belongs to the current connection; using it avoids GetDefault... with the null/stale target
    // that WinUI can supply during a theme transition.
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        var connectedTarget = _connectedTarget;
        var connectedRoot = _connectedRoot;
        var defaults = _defaultConfiguration;
        var configuration = _configuration;
        if (!_connected || connectedTarget is null || connectedRoot is null || _controller is null
            || defaults is null || configuration is null)
            return;

        // Ignore callbacks that identify a different context. A null callback target is handled
        // through the live connection cache, which is the only context this instance owns.
        if (target is not null && !IsSameTarget(connectedTarget, target)) return;
        if (xamlRoot is not null && !ReferenceEquals(xamlRoot, connectedRoot) && !xamlRoot.Equals(connectedRoot)) return;

        if (_usesFallbackConfiguration)
        {
            RefreshFallbackConfiguration();
        }

        configuration.Theme = defaults.Theme;
        configuration.IsHighContrast = defaults.IsHighContrast;
        configuration.IsInputActive = true;
        ApplyAppearance(defaults.Theme);
    }

    private static SystemBackdropConfiguration CreateFallbackConfiguration(XamlRoot xamlRoot) => new()
    {
        Theme = ThemeFromRoot(xamlRoot),
        IsHighContrast = EffectsPolicy.HighContrast,
        IsInputActive = true,
    };

    private void RefreshFallbackConfiguration()
    {
        if (_defaultConfiguration is null || _connectedRoot is null) return;
        _defaultConfiguration.Theme = ThemeFromRoot(_connectedRoot);
        _defaultConfiguration.IsHighContrast = EffectsPolicy.HighContrast;
    }

    private static SystemBackdropConfiguration CreateControllerConfiguration(SystemBackdropConfiguration defaults) => new()
    {
        Theme = defaults.Theme,
        IsHighContrast = defaults.IsHighContrast,
        IsInputActive = true,
    };

    private static SystemBackdropTheme ThemeFromRoot(XamlRoot xamlRoot)
        => xamlRoot.Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark
            ? SystemBackdropTheme.Dark
            : SystemBackdropTheme.Light;

    private void ApplyAppearance(SystemBackdropTheme theme)
    {
        if (_controller is null) return;

        // The Thin acrylic recipe, no colour tint, and a light luminosity layer (0.28 Dark / 0.20 Light; it was 0.40 / 0.30 until 0.1.17).
        // The luminosity layer is what keeps text readable over very bright or very dark
        // desktops (it pulls the brightness behind the glass toward the theme without hiding
        // its colour); the XAML surfaces add only a thin veil on top. Earlier values (tint
        // 0.10-0.15, luminosity 0.45-0.55, plus a ~75% XAML fill) read as milky Vista Aero.
        bool dark = theme == SystemBackdropTheme.Dark;
        _controller.Kind = DesktopAcrylicKind.Thin;
        _controller.TintColor = dark ? Color.FromArgb(0xFF, 0x10, 0x12, 0x17) : Color.FromArgb(0xFF, 0xF7, 0xF9, 0xFC);
        _controller.TintOpacity = 0f;
        _controller.LuminosityOpacity = dark ? 0.28f : 0.20f;
        _controller.FallbackColor = dark ? Color.FromArgb(0xFF, 0x1E, 0x21, 0x26) : Color.FromArgb(0xFF, 0xF2, 0xF4, 0xF7);
    }

    private void DisposeController()
    {
        var controller = _controller;
        var target = _connectedTarget;
        bool removeTarget = _targetAdded;
        _controller = null;
        _targetAdded = false;

        if (controller is null) return;

        try
        {
            if (removeTarget && target is not null)
                controller.RemoveSystemBackdropTarget(target);
        }
        catch (Exception ex) when (ex is ArgumentException or ObjectDisposedException or COMException)
        {
            Shell.LocalLog.Write($"GlassBackdrop: removing the acrylic target failed during cleanup ({ex.GetType().Name}).");
        }
        finally
        {
            try
            {
                controller.Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or COMException)
            {
                Shell.LocalLog.Write($"GlassBackdrop: acrylic controller was already disposed during cleanup ({ex.GetType().Name}).");
            }
        }
    }

    private static bool IsSameTarget(ICompositionSupportsSystemBackdrop? first, ICompositionSupportsSystemBackdrop second)
        => ReferenceEquals(first, second) || first is not null && first.Equals(second);
}
