using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>A stable picker item. No device identity is inferred from proximity or selection.</summary>
public sealed class BroadcastSourceViewModel : ObservableObject
{
    private string _batteryText = "";
    private string _signalText = "";
    private IdentityConfidence _identity;
    private readonly ushort? _modelId;

    public BroadcastSourceViewModel(BroadcastSource source)
    {
        Id = source.Id;
        Label = $"Source {source.Number} · {source.ModelName} (0x{source.ModelId:X4})";
        _identity = source.Identity;
        _modelId = source.ModelId;
        Update(source);
    }

    public Guid Id { get; }
    public string Label { get; }
    // Picker items show this text; it is also the name screen readers announce for the item.
    public override string ToString() => Label;
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }
    public string SignalText { get => _signalText; private set => Set(ref _signalText, value); }

    /// <summary>How likely this source is the selected paired AirPods. Never a proof.</summary>
    public IdentityConfidence Identity { get => _identity; private set => Set(ref _identity, value); }

    /// <summary>"Your AirPods (likely)" (or "Your Beats (likely)") at Probable or better, otherwise "identity unconfirmed".</summary>
    public string IdentityText => _identity.IsLikely() ? IdentityConfidenceText.LikelyFor(AppleModelCatalog.FamilyNounOf(_modelId)) : IdentityConfidenceText.For(_identity);

    internal void Update(BroadcastSource source)
    {
        if (_identity != source.Identity)
        {
            Identity = source.Identity;
            Raise(nameof(IdentityText));
        }
        // A single-battery model reports its one level in the left slot; a model whose case reports no battery has no case line.
        var left = Percent(source.LeftBattery);
        var right = Percent(source.RightBattery);
        BatteryText = source.Layout switch
        {
            BatteryLayout.SingleBattery => $"Approximate battery\nBattery: {left}",
            BatteryLayout.EarbudsNoCaseBattery => $"Approximate battery\nLeft: {left}    Right: {right}",
            _ => $"Approximate battery\nLeft: {left}    Right: {right}\nCase: {Percent(source.CaseBattery)}",
        };
        SignalText = $"Last heard {HubViewModel.FormatClock(source.LastHeardAt)}"
            + (source.Rssi is short rssi ? $" · Signal {rssi} dBm" : "");
    }

    private static string Percent(int? value) => value is int level ? (level == 0 ? "<10%" : $"~{level}%") : "not reported";
}
