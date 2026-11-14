using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.State;

/// <summary>
/// Holds the latest state for the ONE device the user selected. Drops duplicate and
/// out-of-order updates (same or lower <see cref="DeviceState.Sequence"/>) and any state
/// for a different device id, so a nearby stranger's AirPods can never drive the UI.
/// One store per transport instance: sequence numbers restart with a new transport.
/// </summary>
public sealed class DeviceStateStore
{
    private readonly object _gate = new();
    private DeviceState? _current;

    public DeviceStateStore(string selectedDeviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(selectedDeviceId);
        SelectedDeviceId = selectedDeviceId;
    }

    public string SelectedDeviceId { get; }

    public DeviceState? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Apply <paramref name="next"/> if it is newer and for the selected device.</summary>
    /// <param name="next">The newly reported state.</param>
    /// <param name="previous">The state it replaced (null for the first one).</param>
    /// <returns>True when the state was applied.</returns>
    public bool TryApply(DeviceState next, out DeviceState? previous)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            previous = _current;
            if (!string.Equals(next.DeviceId, SelectedDeviceId, StringComparison.Ordinal)) return false;
            if (_current is { } cur && next.Sequence <= cur.Sequence) return false;
            _current = next;
            return true;
        }
    }
}
