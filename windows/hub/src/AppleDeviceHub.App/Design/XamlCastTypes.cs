using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Native AOT: WinUI hands some objects back as a plain <c>object</c> (resource lookups, event senders, template
/// parts, SelectedItem). CsWinRT picks the .NET wrapper type for such an object by its WinRT class name at run time,
/// which only finds types compiled with reflection metadata; any other type comes back as its nearest known base
/// class, so a cast such as <c>(Style)Resources["..."]</c> throws and <c>as Style</c> silently returns null.
/// Every WinUI type this app casts such an object to is listed here. Add a type when adding a new cast of that kind.
/// </summary>
internal static class XamlCastTypes
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(Style))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(Border))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(RadioButton))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof(Control))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(ComboBox))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(ComboBoxItem))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof(FrameworkElement))]
    // Design/ControlFeel.cs walks a ToggleSwitch / Slider template (visual-tree children come back as DependencyObject).
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(Grid))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(Thumb))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(Rectangle))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(Ellipse))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(ContentPresenter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(TickBar))]
    internal static void Keep() { }
}
