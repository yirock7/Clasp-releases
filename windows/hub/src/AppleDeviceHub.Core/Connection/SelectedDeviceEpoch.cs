namespace AppleDeviceHub.Core.Connection;

/// <summary>
/// Identifies the selected paired device at the time an asynchronous event was queued. Work may
/// apply only while both the selection id and its generation still match.
/// </summary>
public readonly record struct SelectedDeviceEpoch(string? DeviceId, int Generation)
{
    /// <summary>Whether queued work still belongs to the currently selected paired device.</summary>
    public bool IsCurrent(string? selectedDeviceId, int currentGeneration)
        => !string.IsNullOrWhiteSpace(DeviceId)
            && Generation == currentGeneration
            && string.Equals(DeviceId, selectedDeviceId, StringComparison.Ordinal);
}
