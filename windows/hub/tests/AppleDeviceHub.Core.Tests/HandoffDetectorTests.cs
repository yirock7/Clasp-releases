using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

public class HandoffDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static HandoffBroadcast Active(DateTimeOffset heard, IdentityConfidence id = IdentityConfidence.LikelyYours)
        => new(id, true, heard);

    private static HandoffDetector Disconnected(out DateTimeOffset disconnectAt)
    {
        var d = new HandoffDetector();
        d.ObserveConnection(true, T0);
        disconnectAt = T0.AddSeconds(1);
        d.ObserveConnection(false, disconnectAt);
        return d;
    }

    /// <summary>Feeds one active sighting per second from start for the given seconds; returns the first time it fired.</summary>
    private static DateTimeOffset? Feed(HandoffDetector d, DateTimeOffset start, int seconds, IdentityConfidence id = IdentityConfidence.LikelyYours)
    {
        for (int s = 0; s <= seconds; s++)
        {
            var now = start.AddSeconds(s);
            d.ObserveBroadcast(Active(now, id), now);
            if (d.Evaluate(now)) return now;
        }
        return null;
    }

    [Fact]
    public void First_connection_reading_is_only_a_baseline()
    {
        var d = new HandoffDetector();
        d.ObserveConnection(false, T0);
        Assert.Equal(HandoffState.Idle, d.State);
    }

    [Fact]
    public void Sustained_activity_after_disconnect_shows_notice()
    {
        var d = Disconnected(out var at);
        var fired = Feed(d, at.AddSeconds(1), 10);
        Assert.NotNull(fired);
        Assert.True(fired >= at.AddSeconds(6));
        Assert.Equal(HandoffState.Shown, d.State);
    }

    [Fact]
    public void LikelyYours_also_qualifies()
    {
        var d = Disconnected(out var at);
        Assert.NotNull(Feed(d, at.AddSeconds(1), 10, IdentityConfidence.LikelyYours));
    }

    [Fact]
    public void Probable_identity_without_an_independent_clue_never_shows_a_handoff_notice()
    {
        var d = Disconnected(out var at);
        Assert.Null(Feed(d, at.AddSeconds(1), 10, IdentityConfidence.Probable));
        Assert.Equal(HandoffState.Cancelled, d.State);
    }

    [Fact]
    public void Brief_activity_is_not_enough()
    {
        var d = Disconnected(out var at);
        Assert.Null(Feed(d, at.AddSeconds(1), 3));
        Assert.Equal(HandoffState.Watching, d.State);
    }

    [Fact]
    public void A_single_sighting_never_triggers_even_much_later()
    {
        var d = Disconnected(out var at);
        var heard = at.AddSeconds(1);
        d.ObserveBroadcast(Active(heard), heard);
        Assert.False(d.Evaluate(heard.AddSeconds(7)));
    }

    [Fact]
    public void Notice_fires_once_per_episode()
    {
        var d = Disconnected(out var at);
        var fired = Feed(d, at.AddSeconds(1), 10)!.Value;
        for (int s = 1; s < 20; s++)
        {
            var now = fired.AddSeconds(s);
            d.ObserveBroadcast(Active(now), now);
            Assert.False(d.Evaluate(now));
        }
    }

    [Fact]
    public void A_new_disconnect_episode_can_fire_again()
    {
        var d = Disconnected(out var at);
        var fired = Feed(d, at.AddSeconds(1), 10)!.Value;
        d.ObserveConnection(true, fired.AddSeconds(30));
        var second = fired.AddSeconds(60);
        d.ObserveConnection(false, second);
        Assert.NotNull(Feed(d, second.AddSeconds(1), 10));
    }

    [Fact]
    public void User_initiated_disconnect_never_shows()
    {
        var d = new HandoffDetector();
        d.ObserveConnection(true, T0);
        d.NoteUserDisconnect(T0.AddSeconds(1));
        d.ObserveConnection(false, T0.AddSeconds(3));
        Assert.Equal(HandoffState.Cancelled, d.State);
        Assert.Null(Feed(d, T0.AddSeconds(4), 20));
    }

    [Fact]
    public void User_disconnect_seen_just_after_the_transition_also_cancels()
    {
        var d = Disconnected(out var at);
        d.NoteUserDisconnect(at.AddSeconds(2));
        Assert.Null(Feed(d, at.AddSeconds(3), 20));
    }

    [Fact]
    public void Old_user_disconnect_does_not_suppress()
    {
        var d = new HandoffDetector();
        d.ObserveConnection(true, T0);
        d.NoteUserDisconnect(T0);
        d.ObserveConnection(false, T0.AddSeconds(30));
        Assert.NotNull(Feed(d, T0.AddSeconds(31), 10));
    }

    [Fact]
    public void Pods_going_into_the_case_cancels()
    {
        var d = Disconnected(out var at);
        Feed(d, at.AddSeconds(1), 3);
        var now = at.AddSeconds(5);
        d.ObserveBroadcast(new HandoffBroadcast(IdentityConfidence.Probable, false, now), now);
        Assert.Equal(HandoffState.Cancelled, d.State);
        Assert.Null(Feed(d, at.AddSeconds(6), 20));
    }

    [Fact]
    public void Broadcast_vanishing_cancels()
    {
        var d = Disconnected(out var at);
        Feed(d, at.AddSeconds(1), 3);
        d.ObserveBroadcast(null, at.AddSeconds(6));
        Assert.Equal(HandoffState.Cancelled, d.State);
    }

    [Fact]
    public void Stale_last_sighting_cancels_on_evaluate()
    {
        var d = Disconnected(out var at);
        Feed(d, at.AddSeconds(1), 3);
        Assert.False(d.Evaluate(at.AddSeconds(30)));
        Assert.Equal(HandoffState.Cancelled, d.State);
    }

    [Fact]
    public void No_match_yet_just_waits_until_timeout()
    {
        var d = Disconnected(out var at);
        d.ObserveBroadcast(null, at.AddSeconds(2));
        Assert.Equal(HandoffState.Watching, d.State);
        Assert.False(d.Evaluate(at.AddSeconds(60)));
        Assert.Equal(HandoffState.Cancelled, d.State);
    }

    [Fact]
    public void Identity_unknown_is_suppressed()
    {
        var d = Disconnected(out var at);
        Assert.Null(Feed(d, at.AddSeconds(1), 20, IdentityConfidence.Unknown));
        Assert.Equal(HandoffState.Cancelled, d.State);
    }

    [Fact]
    public void Identity_dropping_mid_episode_cancels()
    {
        var d = Disconnected(out var at);
        Feed(d, at.AddSeconds(1), 3);
        var now = at.AddSeconds(5);
        d.ObserveBroadcast(Active(now, IdentityConfidence.Unknown), now);
        Assert.Equal(HandoffState.Cancelled, d.State);
    }

    [Fact]
    public void Windows_reconnecting_cancels()
    {
        var d = Disconnected(out var at);
        Feed(d, at.AddSeconds(1), 3);
        d.ObserveConnection(true, at.AddSeconds(5));
        Assert.Equal(HandoffState.Idle, d.State);
        Assert.False(d.Evaluate(at.AddSeconds(8)));
    }

    [Fact]
    public void Disabled_setting_never_shows()
    {
        var d = new HandoffDetector { Enabled = false };
        d.ObserveConnection(true, T0);
        d.ObserveConnection(false, T0.AddSeconds(1));
        Assert.Null(Feed(d, T0.AddSeconds(2), 20));
    }

    [Fact]
    public void Disabling_mid_episode_stops_the_notice()
    {
        var d = Disconnected(out var at);
        Feed(d, at.AddSeconds(1), 3);
        d.Enabled = false;
        Assert.Null(Feed(d, at.AddSeconds(5), 20));
    }

    [Fact]
    public void Packets_from_before_the_disconnect_are_ignored()
    {
        var d = Disconnected(out var at);
        for (int s = 0; s < 15; s++)
        {
            var now = at.AddSeconds(s);
            d.ObserveBroadcast(Active(at.AddSeconds(-2)), now);
            Assert.False(d.Evaluate(now));
        }
    }

    [Fact]
    public void Repeated_same_packet_counts_once()
    {
        var d = Disconnected(out var at);
        var heard = at.AddSeconds(1);
        for (int s = 0; s < 6; s++) d.ObserveBroadcast(Active(heard), heard.AddSeconds(s));
        Assert.False(d.Evaluate(heard.AddSeconds(6)));
    }

    [Fact]
    public void FromSource_maps_case_state_and_identity()
    {
        BroadcastSource Make(bool? inCase) => new(Guid.NewGuid(), 1, 0x2720, "AirPods Pro 3", -50, 80, 80, 80, T0,
            ThisPodInCase: inCase, Identity: IdentityConfidence.Probable);
        Assert.True(HandoffBroadcast.FromSource(Make(false)).OutOfCase);
        Assert.False(HandoffBroadcast.FromSource(Make(true)).OutOfCase);
        Assert.False(HandoffBroadcast.FromSource(Make(null)).OutOfCase);
        Assert.Equal(IdentityConfidence.Probable, HandoffBroadcast.FromSource(Make(false)).Identity);
    }

    [Fact]
    public void Notice_wording_is_honest()
    {
        Assert.Contains("probably", HandoffNoticeText.Detail);
        Assert.Contains("another device", HandoffNoticeText.Title);
        Assert.Equal("", HandoffNoticeText.Status(false, false, false, null));
        Assert.Contains("again", HandoffNoticeText.Status(false, true, true, null));
        Assert.Equal("x", HandoffNoticeText.Status(false, false, true, "x"));
    }
}
