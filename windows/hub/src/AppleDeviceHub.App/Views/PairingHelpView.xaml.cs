using AppleDeviceHub.Core.Ble;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Views;

/// <summary>One numbered step. <see cref="Spoken"/> is what a screen reader says for the row.</summary>
public sealed record PairingStepItem(string Number, string Text)
{
    public string Spoken => $"{Number} {Text}";
}

/// <summary>
/// The shared "How to pair your AirPods" help. The host calls <see cref="SetModel"/> with the model it knows (a name
/// from the catalog, or null) and <see cref="SetExpanded"/> when it wants to open or close it; after that the user's
/// own choices stand until the host changes them again.
/// </summary>
public sealed partial class PairingHelpView : UserControl
{
    private readonly PairingDefaultTracker _default = new();
    private bool _loading;
    private bool _active = true;

    // WinRT cannot hand a plain IReadOnlyList to ItemsSource under Native AOT; a List is marshalled fine.
    private readonly List<PairingGuide> _guides = new(PairingInstructions.Guides);

    // Null when the decorative animation could not be built; the steps still work without it.
    private PairingIllustration? _illustration;

    private PairingIllustration? Illustration => _illustration;

    public PairingHelpView()
    {
        InitializeComponent();
        try
        {
            _illustration = new PairingIllustration();
            IllustrationHost.Child = _illustration;
        }
        catch (Exception ex)
        {
            _illustration = null;
            Shell.LocalLog.Write("Pairing illustration unavailable: " + ex.Message);
        }
        _loading = true;
        try
        {
            ModelPicker.ItemsSource = _guides;
            ModelPicker.SelectedIndex = 0;
        }
        finally { _loading = false; }
        ShowGuide(_guides[0]);
    }

    public bool IsExpanded => HelpExpander.IsExpanded;

    /// <summary>
    /// False pauses the illustration (the window is hidden or the page is not showing); true lets it run again
    /// while the help is open. A collapsed help never animates.
    /// </summary>
    public void SetActive(bool active)
    {
        _active = active;
        UpdateAnimation();
    }

    // Expanding fires before IsExpanded flips, so say which way it is going.
    private void OnExpanding(Expander sender, object args) => UpdateAnimation(expandedOverride: true);

    private void OnCollapsed(Expander sender, object args) => UpdateAnimation(expandedOverride: false);

    private void UpdateAnimation(bool? expandedOverride = null)
    {
        bool expanded = expandedOverride ?? HelpExpander.IsExpanded;
        if (Illustration is not { } art) return;
        try
        {
            art.Enabled = _active && expanded;
            if (art.Enabled) art.Replay();
            else art.Stop();
        }
        catch (Exception ex) { DropIllustration(ex); }
    }

    /// <summary>Opens or closes the help.</summary>
    public void SetExpanded(bool expanded) => HelpExpander.IsExpanded = expanded;

    /// <summary>
    /// Picks the guide for a catalog model name, but only when the known model really changed and the user has not
    /// chosen one by hand. Routine refreshes repeat the same name and leave the picker alone.
    /// </summary>
    public void SetModel(string? modelName)
    {
        if (!_default.ShouldApply(modelName)) return;
        if (PairingInstructions.FindByModelName(modelName) is not { } guide) return;
        if (ReferenceEquals(ModelPicker.SelectedItem, guide)) return;
        _loading = true;
        try { ModelPicker.SelectedItem = guide; }
        finally { _loading = false; }
        ShowGuide(guide);
    }

    private void OnModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelPicker.SelectedItem is not PairingGuide guide) return;
        _default.UserPicked();
        ShowGuide(guide);
    }

    private void ShowGuide(PairingGuide guide)
    {
        GuideTitle.Text = guide.Title;
        try { Illustration?.Show(PairingAnimations.For(guide)); }
        catch (Exception ex) { DropIllustration(ex); }
        UpdateAnimation();
        var items = new List<PairingStepItem>(guide.Steps.Count);
        for (int i = 0; i < guide.Steps.Count; i++) items.Add(new PairingStepItem($"{i + 1}.", guide.Steps[i]));
        StepList.ItemsSource = items;
    }

    // The picture is decoration: if it misbehaves, log once and carry on with the text steps alone.
    private void DropIllustration(Exception ex)
    {
        _illustration = null;
        IllustrationHost.Child = null;
        Shell.LocalLog.Write("Pairing illustration disabled: " + ex.Message);
    }
}
