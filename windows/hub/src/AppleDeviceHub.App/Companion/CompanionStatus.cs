using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Commands;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.App.Companion;

/// <summary>
/// Plain-language status lines the settings page shows for the companion features. Everything is
/// set on the UI thread by <see cref="CompanionHost"/>; the page binds to it (OneWay). Empty text
/// means "nothing to say", never a guess.
/// </summary>
internal sealed class CompanionStatus : ObservableObject
{
    private bool _hasSelectedDevice;
    private bool _repairIsRunning;
    private string _repairText = "";
    private bool _repairSucceeded;
    private string _handsfreeText = "";
    private string _lowLatencyText = "";
    private string _linksText = "";
    private string _stemText = "";
    private bool _perAppSupported = true;
    private string _perAppSupportText = "";
    private PhoneStreamingState _phoneState = PhoneStreamingStates.Idle();
    private IReadOnlyDictionary<ClaspCommand, string> _hotkeyProblems = new Dictionary<ClaspCommand, string>();

    /// <summary>A paired AirPods is selected (the Fix connection button needs one).</summary>
    public bool HasSelectedDevice { get => _hasSelectedDevice; set { if (Set(ref _hasSelectedDevice, value)) Raise(nameof(CanStartRepair)); } }

    /// <summary>Fix connection is running (Bluetooth is being switched off and on).</summary>
    public bool RepairIsRunning { get => _repairIsRunning; set { if (Set(ref _repairIsRunning, value)) Raise(nameof(CanStartRepair)); } }

    public bool CanStartRepair => _hasSelectedDevice && !_repairIsRunning;

    /// <summary>Progress while running, then the final result message.</summary>
    public string RepairText { get => _repairText; set => Set(ref _repairText, value); }

    /// <summary>The last finished repair succeeded (success styling).</summary>
    public bool RepairSucceeded { get => _repairSucceeded; set => Set(ref _repairSucceeded, value); }

    public string HandsfreeText { get => _handsfreeText; set => Set(ref _handsfreeText, value); }

    public string LowLatencyText { get => _lowLatencyText; set => Set(ref _lowLatencyText, value); }

    /// <summary>Whether clasp:// links are registered, or why not.</summary>
    public string LinksText { get => _linksText; set => Set(ref _linksText, value); }

    public string StemText { get => _stemText; set => Set(ref _stemText, value); }

    /// <summary>Windows exposes the per-app output interface Clasp knows how to use.</summary>
    public bool PerAppSupported { get => _perAppSupported; set => Set(ref _perAppSupported, value); }

    public string PerAppSupportText { get => _perAppSupportText; set => Set(ref _perAppSupportText, value); }

    public PhoneStreamingState PhoneState { get => _phoneState; set => Set(ref _phoneState, value); }

    /// <summary>Per-shortcut problems from the last registration (for example "Another program already uses this shortcut.").</summary>
    public IReadOnlyDictionary<ClaspCommand, string> HotkeyProblems
    {
        get => _hotkeyProblems;
        set
        {
            _hotkeyProblems = value;
            Raise(nameof(HotkeyProblems));
        }
    }
}
