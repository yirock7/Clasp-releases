using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests;

/// <summary>A burst of transport states queues one UI callback that applies the newest state.</summary>
public class StateCoalescingTests
{
    [Fact]
    public async Task A_thousand_states_queue_one_callback_that_applies_the_last()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var queued = new List<Action>();
        await using var vm = new HubViewModel(transport, transport.DeviceId, queued.Add, time: time);

        queued.Clear(); // anything queued while constructing is not under test
        for (var i = 0; i < 1000; i++)
            transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: i % 101);
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 77);

        Assert.Single(queued);
        queued[0]();
        Assert.Equal(77, vm.AdaptiveStrength);

        // The flag was reset, so the next state queues again.
        queued.Clear();
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 20);
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(20, vm.AdaptiveStrength);
    }
}
