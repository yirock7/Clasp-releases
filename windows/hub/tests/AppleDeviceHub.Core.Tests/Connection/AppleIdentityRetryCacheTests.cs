using AppleDeviceHub.App.Connection;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Connection;

public class AppleIdentityRetryCacheTests
{
    [Fact]
    public void Missing_metadata_is_retried_after_a_bounded_cooldown()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var retryInterval = TimeSpan.FromSeconds(30);
        var cache = new AppleIdentityRetryCache(time, retryInterval);

        Assert.True(cache.TryBeginRead("container-a"));
        cache.RememberMissing("container-a");
        Assert.False(cache.TryBeginRead("container-a"));

        time.Advance(retryInterval - TimeSpan.FromMilliseconds(1));
        Assert.False(cache.TryBeginRead("container-a"));

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(cache.TryBeginRead("container-a"));
        Assert.False(cache.TryBeginRead("container-a"));
    }

    [Fact]
    public void Retry_windows_are_per_container_and_found_metadata_clears_the_cooldown()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new AppleIdentityRetryCache(time, TimeSpan.FromSeconds(30));

        Assert.True(cache.TryBeginRead("container-a"));
        cache.RememberMissing("container-a");
        Assert.True(cache.TryBeginRead("container-b"));
        cache.RememberFound("container-a", 0x2720);

        Assert.True(cache.TryBeginRead("container-a"));
        Assert.False(cache.TryBeginRead("container-a"));
    }

    [Fact]
    public void Transient_miss_preserves_only_the_same_containers_last_model_and_drops_battery()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new AppleIdentityRetryCache(time, TimeSpan.FromSeconds(30));

        Assert.True(cache.TryBeginRead("container-a"));
        cache.RememberFound("container-a", 0x2720);
        cache.RememberMissing("container-a");

        Assert.Equal((ModelId: (ushort?)0x2720, BatteryPercent: (int?)null), cache.TransientFallback("container-a"));
        Assert.Equal((ModelId: (ushort?)null, BatteryPercent: (int?)null), cache.TransientFallback("container-b"));
        Assert.False(cache.TryBeginRead("container-a")); // cooldown snapshots receive the same identity-only fallback
        Assert.Equal((ModelId: (ushort?)0x2720, BatteryPercent: (int?)null), cache.TransientFallback("container-a"));
    }
}
