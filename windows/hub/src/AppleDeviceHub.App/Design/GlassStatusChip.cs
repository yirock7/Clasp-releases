using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// Small status pill: glyph + text for connection, charging, stale, pending, error and Demo.
/// Each <see cref="ChipKind"/> maps to a visual state in GlassStyles.xaml with its own glyph,
/// so colour never carries the meaning alone. The text is exposed to UI Automation through
/// the TextBlock in the template.
/// </summary>
[TemplateVisualState(GroupName = "KindStates", Name = "Neutral")]
[TemplateVisualState(GroupName = "KindStates", Name = "Demo")]
[TemplateVisualState(GroupName = "KindStates", Name = "Success")]
[TemplateVisualState(GroupName = "KindStates", Name = "Pending")]
[TemplateVisualState(GroupName = "KindStates", Name = "Warning")]
[TemplateVisualState(GroupName = "KindStates", Name = "Error")]
public partial class GlassStatusChip : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(GlassStatusChip), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(ChipKind), typeof(GlassStatusChip), new PropertyMetadata(ChipKind.Neutral, OnKindChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ChipKind Kind
    {
        get => (ChipKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateKind();
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((GlassStatusChip)d).UpdateKind();

    private void UpdateKind() => VisualStateManager.GoToState(this, StateName(Kind), false);

    private static string StateName(ChipKind kind) => kind switch
    {
        ChipKind.Demo => "Demo",
        ChipKind.Success => "Success",
        ChipKind.Pending => "Pending",
        ChipKind.Warning => "Warning",
        ChipKind.Error => "Error",
        _ => "Neutral",
    };
}
