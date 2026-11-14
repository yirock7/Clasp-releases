using Microsoft.UI.Xaml;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// XAML helper for the reduced-transparency fallback:
/// <c>&lt;Border design:GlassFallback.ShowWhenOpaque="True" Background="{ThemeResource GlassOpaqueFillBrush}" /&gt;</c>
/// is visible only while <see cref="EffectsPolicy.ReduceTransparency"/> is on, so gaps and
/// insets around glass surfaces never show an unreadable backdrop.
/// </summary>
public sealed class GlassFallback
{
    public static readonly DependencyProperty ShowWhenOpaqueProperty = DependencyProperty.RegisterAttached(
        "ShowWhenOpaque", typeof(bool), typeof(GlassFallback), new PropertyMetadata(false, OnShowWhenOpaqueChanged));

    public static bool GetShowWhenOpaque(DependencyObject obj) => (bool)obj.GetValue(ShowWhenOpaqueProperty);

    public static void SetShowWhenOpaque(DependencyObject obj, bool value) => obj.SetValue(ShowWhenOpaqueProperty, value);

    private static void OnShowWhenOpaqueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true || e.OldValue is true) return;

        void Update() => element.Visibility = EffectsPolicy.ReduceTransparency ? Visibility.Visible : Visibility.Collapsed;
        EventHandler onChanged = (_, _) => Update();

        element.Loaded += (_, _) =>
        {
            EffectsPolicy.Changed -= onChanged;
            EffectsPolicy.Changed += onChanged;
            Update();
        };
        element.Unloaded += (_, _) => EffectsPolicy.Changed -= onChanged;
        Update();
    }
}
