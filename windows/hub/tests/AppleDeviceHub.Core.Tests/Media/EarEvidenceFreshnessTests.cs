using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Media;

/// <summary>
/// EF-IR-03: freshness and order of actionable ear evidence. Ear packets are sent only when
/// placement changes, so the *changed* report must be fresh; the earlier baseline may be old.
/// </summary>
public class EarEvidenceFreshnessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly MediaSnapshot Playing = new("player#1", MediaPlayback.Playing);
    private static readonly MediaSnapshot Paused = new("player#1", MediaPlayback.Paused);

    private static DeviceState Ears(PodPlacement placement, DateTimeOffset earAt, DateTimeOffset? stateAt = null, int battery = 50)
    {
        var at = stateAt ?? earAt;
        return new DeviceState("dev", "AirPods", "AirPods Pro", DataOrigin.Hardware, 1, at,
            DeviceCapabilities.EarDetection | DeviceCapabilities.Battery,
            LinkState.Unknown with { ProtocolSession = Observation<bool>.At(true, at) },
            new BatteryState(BatteryComponent.From(battery, false, at), BatteryComponent.Unknown, BatteryComponent.Unknown),
            ListeningState.Unknown, default, default)
        {
            Ears = new EarState(Observation<PodPlacement>.At(placement, earAt), Observation<PodPlacement>.At(placement, earAt)),
        };
    }

    private static EarMediaPolicy Policy() => new() { Enabled = true };

    [Fact]
    public void Long_playback_then_a_fresh_removal_still_pauses()
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0), Playing, false, T0);
        // Two hours of listening; battery reports keep arriving with the old ear evidence.
        for (var minutes = 10; minutes <= 120; minutes += 10)
        {
            var now = T0.AddMinutes(minutes);
            Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(Ears(PodPlacement.InEar, T0, now, battery: 50 - minutes / 10), Playing, false, now).Kind);
        }
        var removedAt = T0.AddHours(2).AddSeconds(1);
        Assert.Equal(EarMediaActionKind.Pause, p.OnDeviceState(Ears(PodPlacement.OutOfEar, removedAt), Playing, false, removedAt.AddMilliseconds(200)).Kind);
    }

    [Fact]
    public void A_baseline_first_seen_long_after_it_was_reported_is_still_a_valid_baseline()
    {
        var p = Policy();
        var now = T0.AddHours(1);
        p.OnDeviceState(Ears(PodPlacement.InEar, T0, now), Playing, false, now); // app attached mid-session
        Assert.Equal(EarMediaActionKind.Pause, p.OnDeviceState(Ears(PodPlacement.OutOfEar, now), Playing, false, now).Kind);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(3600)]
    public void A_delayed_removal_report_does_not_pause_and_resets_the_baseline(int delaySeconds)
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0), Playing, false, T0);
        var removedAt = T0.AddMinutes(1);
        var action = p.OnDeviceState(Ears(PodPlacement.OutOfEar, removedAt), Playing, false, removedAt.AddSeconds(delaySeconds));
        Assert.Equal(EarMediaActionKind.None, action.Kind);
        Assert.Equal("ear report too old to act on", action.Reason);

        // The next fresh report only re-establishes a baseline; it is not judged against the stale one.
        var backAt = removedAt.AddSeconds(delaySeconds + 1);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(Ears(PodPlacement.InEar, backAt), Playing, false, backAt).Kind);
    }

    [Fact]
    public void A_report_within_the_freshness_limit_is_acted_on()
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0), Playing, false, T0);
        var removedAt = T0.AddMinutes(1);
        Assert.Equal(EarMediaActionKind.Pause, p.OnDeviceState(Ears(PodPlacement.OutOfEar, removedAt), Playing, false, removedAt.AddSeconds(5)).Kind);
    }

    [Fact]
    public void A_report_dated_in_the_future_is_rejected()
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0), Playing, false, T0);
        var action = p.OnDeviceState(Ears(PodPlacement.OutOfEar, T0.AddMinutes(10)), Playing, false, T0.AddSeconds(1));
        Assert.Equal(EarMediaActionKind.None, action.Kind);
        Assert.Equal("ear report is dated in the future", action.Reason);
    }

    [Fact]
    public void Small_clock_skew_is_tolerated()
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0), Playing, false, T0);
        var removedAt = T0.AddSeconds(31);
        Assert.Equal(EarMediaActionKind.Pause, p.OnDeviceState(Ears(PodPlacement.OutOfEar, removedAt), Playing, false, T0.AddSeconds(30)).Kind);
    }

    [Fact]
    public void A_changed_placement_older_than_the_last_report_is_out_of_order()
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0.AddSeconds(10)), Playing, false, T0.AddSeconds(10));
        var action = p.OnDeviceState(Ears(PodPlacement.OutOfEar, T0.AddSeconds(5)), Playing, false, T0.AddSeconds(11));
        Assert.Equal(EarMediaActionKind.None, action.Kind);
        Assert.Equal("ear report out of order", action.Reason);
    }

    [Fact]
    public void Unusable_new_evidence_cancels_a_pending_resume()
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0), Playing, false, T0);
        var pause = p.OnDeviceState(Ears(PodPlacement.OutOfEar, T0.AddSeconds(1)), Playing, false, T0.AddSeconds(1));
        p.PauseIssued(pause.SessionId!);
        p.OnMediaChanged(Paused, T0.AddSeconds(2));
        Assert.True(p.HasPendingResume);
        var epoch = p.Epoch;

        // The return arrives an hour late: it must neither resume nor leave the claim pending.
        var lateBack = p.OnDeviceState(Ears(PodPlacement.InEar, T0.AddSeconds(30)), Paused, false, T0.AddHours(1));
        Assert.Equal(EarMediaActionKind.None, lateBack.Kind);
        Assert.False(p.HasPendingResume);
        Assert.True(p.Epoch > epoch); // queued actions from before are invalidated too
    }

    [Fact]
    public void A_resent_report_with_another_update_is_not_an_ear_event()
    {
        var p = Policy();
        p.OnDeviceState(Ears(PodPlacement.InEar, T0), Playing, false, T0);
        var pause = p.OnDeviceState(Ears(PodPlacement.OutOfEar, T0.AddSeconds(1)), Playing, false, T0.AddSeconds(1));
        p.PauseIssued(pause.SessionId!);
        p.OnMediaChanged(Paused, T0.AddSeconds(2));

        // Battery updates long after, still carrying the removal report: no action, claim kept.
        var now = T0.AddMinutes(5);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(Ears(PodPlacement.OutOfEar, T0.AddSeconds(1), now, battery: 40), Paused, false, now).Kind);
        Assert.True(p.HasPendingResume);
        Assert.Equal(EarMediaActionKind.Resume, p.OnDeviceState(Ears(PodPlacement.InEar, now), Paused, false, now).Kind);
    }
}
