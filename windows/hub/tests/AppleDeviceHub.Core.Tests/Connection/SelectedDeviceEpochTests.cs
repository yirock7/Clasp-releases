using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.Core.Tests.Connection;

public sealed class SelectedDeviceEpochTests
{
    [Fact]
    public void Queued_battery_event_is_dropped_after_same_model_device_selection_changes()
    {
        var queuedForFirstDevice = new SelectedDeviceEpoch("paired-device-one", Generation: 12);

        Assert.False(queuedForFirstDevice.IsCurrent("paired-device-two", currentGeneration: 13));
        Assert.True(queuedForFirstDevice.IsCurrent("paired-device-one", currentGeneration: 12));
    }

    [Fact]
    public void Queued_event_is_dropped_when_selection_changes_away_and_back()
    {
        var queuedBeforeReconnect = new SelectedDeviceEpoch("paired-device", Generation: 4);

        Assert.False(queuedBeforeReconnect.IsCurrent("paired-device", currentGeneration: 6));
    }
}
