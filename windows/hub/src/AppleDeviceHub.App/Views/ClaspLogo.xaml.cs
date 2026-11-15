using AppleDeviceHub.App.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// The full-colour Clasp logo (see ClaspLogo.xaml). Size it with Width/Height; it keeps its proportions.
/// In High Contrast it shows the monochrome <see cref="ClaspGlyph"/> instead.
/// </summary>
public sealed partial class ClaspLogo : UserControl
{
    public ClaspLogo()
    {
        InitializeComponent();
        Picture.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ClaspLogo.png")));
        Loaded += (_, _) =>
        {
            EffectsPolicy.Changed += OnEffectsChanged;
            Apply();
        };
        Unloaded += (_, _) => EffectsPolicy.Changed -= OnEffectsChanged;
    }

    private void OnEffectsChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        bool contrast = EffectsPolicy.HighContrast;
        Picture.Visibility = contrast ? Visibility.Collapsed : Visibility.Visible;
        ContrastGlyph.Visibility = contrast ? Visibility.Visible : Visibility.Collapsed;
    }
}
