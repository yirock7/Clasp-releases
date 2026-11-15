using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Battery;

public class LastSeenDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private static LastSeenTracker New() => new(new InMemoryBatteryHistoryStore());

    [Fact]
    public void A_probable_sighting_updates_last_seen()
    {
        var t = New();
        Assert.True(t.Observe("d", IdentityConfidence.Probable, -50, Now.AddMinutes(-37)));
        Assert.True(t.Observe("d", IdentityConfidence.Probable, -90, Now.AddMinutes(-6)));
        Assert.Equal(Now.AddMinutes(-6), t.Get("d")!.Time);
        Assert.Equal(SignalBucket.Far, t.Get("d")!.Signal);
    }

    [Fact]
    public void Unknown_sightings_never_update_it()
    {
        var t = New();
        Assert.False(t.Observe("d", IdentityConfidence.Unknown, -50, Now));
        Assert.Null(t.Get("d"));
        t.Observe("d", IdentityConfidence.Probable, -50, Now.AddHours(-1));
        Assert.False(t.Observe("d", IdentityConfidence.Unknown, -50, Now));
        Assert.Equal(Now.AddHours(-1), t.Get("d")!.Time);
    }

    [Fact]
    public void A_newer_shown_reading_wins_with_the_current_signal()
    {
        var t = New();
        t.Observe("d", IdentityConfidence.LikelyYours, -90, Now.AddDays(-1));
        var seen = t.Effective("d", Now, Now.AddMinutes(-6), -50)!;
        Assert.Equal(Now.AddMinutes(-6), seen.Time);
        Assert.Equal(SignalBucket.Near, seen.Signal);
        Assert.NotNull(t.Text("d", Now, Now.AddMinutes(-6), -50));
    }

    [Fact]
    public void A_reading_with_no_signal_keeps_the_stored_bucket()
    {
        var t = New();
        t.Observe("d", IdentityConfidence.LikelyYours, -90, Now.AddDays(-1));
        Assert.Equal(SignalBucket.Far, t.Effective("d", Now, Now.AddMinutes(-6), null)!.Signal);
    }

    [Fact]
    public void A_reading_with_no_stored_record_still_shows()
    {
        Assert.Equal(Now.AddMinutes(-3), New().Effective("d", Now, Now.AddMinutes(-3), null)!.Time);
    }

    [Fact]
    public void A_stale_record_with_no_readings_shows_the_stored_time()
    {
        var t = New();
        t.Observe("d", IdentityConfidence.LikelyYours, -50, Now.AddDays(-1));
        Assert.Equal(Now.AddDays(-1), t.Effective("d", Now, null, -50)!.Time);
        Assert.Equal(Now.AddDays(-1), t.Effective("d", Now, Now.AddDays(-2), -50)!.Time);
    }

    [Fact]
    public void A_future_reading_is_ignored()
    {
        var t = New();
        t.Observe("d", IdentityConfidence.LikelyYours, -50, Now.AddHours(-1));
        Assert.Equal(Now.AddHours(-1), t.Effective("d", Now, Now.AddMinutes(5), null)!.Time);
    }

    [Fact]
    public void The_persisted_record_survives_a_restart()
    {
        var store = new InMemoryBatteryHistoryStore();
        var t = new LastSeenTracker(store);
        t.Observe("d", IdentityConfidence.Probable, -70, Now.AddMinutes(-6));
        t.Flush();
        var again = new LastSeenTracker(store);
        again.Load();
        Assert.Equal(Now.AddMinutes(-6).ToUnixTimeSeconds(), again.Get("d")!.Time.ToUnixTimeSeconds());
    }
}
