using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Battery;

public class LastSeenRetentionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_the_eight_most_recently_heard_devices_are_kept()
    {
        var tracker = new LastSeenTracker(new InMemoryBatteryHistoryStore());
        for (int i = 0; i < 20; i++)
            tracker.Observe($"dev{i}", IdentityConfidence.LikelyYours, -50, T0.AddMinutes(i * 5));
        var kept = Enumerable.Range(0, 20).Where(i => tracker.Get($"dev{i}") is not null).ToList();
        Assert.Equal(LastSeenTracker.MaxDevices, kept.Count);
        Assert.Contains(19, kept);
        Assert.DoesNotContain(0, kept);
    }

    [Fact]
    public void An_oversized_file_is_trimmed_on_load()
    {
        var store = new InMemoryBatteryHistoryStore();
        var lines = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"dev{i},{T0.AddMinutes(i).ToUnixTimeSeconds()},3"));
        store.Write(LastSeenTracker.StoreName, "clasp-last-seen 1\n" + lines + "\n");
        var tracker = new LastSeenTracker(store);
        tracker.Load();
        Assert.Equal(8, Enumerable.Range(0, 30).Count(i => tracker.Get($"dev{i}") is not null));
        Assert.NotNull(tracker.Get("dev29"));
    }

    [Fact]
    public void Clear_forgets_every_device_and_flush_writes_the_empty_file()
    {
        var store = new InMemoryBatteryHistoryStore();
        var tracker = new LastSeenTracker(store);
        tracker.Observe("dev", IdentityConfidence.LikelyYours, -50, T0);
        tracker.Flush();
        tracker.Clear();
        Assert.Null(tracker.Get("dev"));
        Assert.True(tracker.Flush());
        var loaded = new LastSeenTracker(store);
        loaded.Load();
        Assert.Null(loaded.Get("dev"));
    }
}
