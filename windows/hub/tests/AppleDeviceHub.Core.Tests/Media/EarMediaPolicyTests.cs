using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Media;

public class EarMediaPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly MediaSnapshot Playing = new("spotify#1", MediaPlayback.Playing);
    private static readonly MediaSnapshot Paused = new("spotify#1", MediaPlayback.Paused);

    private const PodPlacement In = PodPlacement.InEar;
    private const PodPlacement Out = PodPlacement.OutOfEar;
    private const PodPlacement Case = PodPlacement.InCase;

    private static DeviceState State(
        PodPlacement? primary,
        PodPlacement? secondary,
        DataOrigin origin = DataOrigin.Hardware,
        bool session = true,
        string deviceId = "dev",
        DeviceCapabilities caps = DeviceCapabilities.EarDetection,
        DateTimeOffset? at = null)
    {
        // Ear reports carry their own observation time; by default the test clock's T0.
        var when = at ?? T0;
        Observation<PodPlacement> O(PodPlacement? p) => p is { } v ? Observation<PodPlacement>.At(v, when) : default;
        var link = LinkState.Unknown with { ProtocolSession = Observation<bool>.At(session, T0) };
        return new DeviceState(deviceId, "AirPods", "AirPods Pro", origin, 1, T0, caps, link,
            BatteryState.Unknown, ListeningState.Unknown, default, default)
        {
            Ears = new EarState(O(primary), O(secondary)),
        };
    }

    private static EarMediaPolicy Enabled(EarPauseTrigger trigger = EarPauseTrigger.AllRemoved)
        => new() { Enabled = true, Trigger = trigger };

    /// <summary>Wear, remove (expect pause), and confirm the pause through Windows.</summary>
    private static EarMediaPolicy PausedByClasp(EarPauseTrigger trigger = EarPauseTrigger.AllRemoved)
    {
        var p = Enabled(trigger);
        p.OnDeviceState(State(In, In), Playing, false, T0);
        var pause = p.OnDeviceState(State(Out, Case), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.Pause, pause.Kind);
        p.PauseIssued(pause.SessionId!);
        p.OnMediaChanged(Paused, T0);
        Assert.True(p.HasPendingResume);
        return p;
    }

    [Fact]
    public void Disabled_by_default_and_never_acts()
    {
        var p = new EarMediaPolicy();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out), Playing, false, T0).Kind);
    }

    [Fact]
    public void Removing_both_buds_while_playing_pauses_that_session()
    {
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        var action = p.OnDeviceState(State(Out, Case), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.Pause, action.Kind);
        Assert.Equal("spotify#1", action.SessionId);
    }

    [Fact]
    public void First_report_is_not_a_transition()
    {
        var p = Enabled();
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out), Playing, false, T0).Kind);
    }

    [Fact]
    public void Removing_one_bud_does_not_pause_by_default_but_does_with_AnyRemoved()
    {
        var conservative = Enabled();
        conservative.OnDeviceState(State(In, In), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.None, conservative.OnDeviceState(State(In, Out), Playing, false, T0).Kind);

        var appleLike = Enabled(EarPauseTrigger.AnyRemoved);
        appleLike.OnDeviceState(State(In, In), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.Pause, appleLike.OnDeviceState(State(In, Out), Playing, false, T0).Kind);
    }

    [Theory]
    [InlineData(MediaPlayback.Paused)]
    [InlineData(MediaPlayback.Stopped)]
    [InlineData(MediaPlayback.Unknown)]
    public void Nothing_playing_means_no_pause(MediaPlayback playback)
    {
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out), new("spotify#1", playback), false, T0).Kind);
    }

    [Fact]
    public void Broadcast_or_demo_state_passed_to_OnDeviceState_is_never_used()
    {
        foreach (var origin in new[] { DataOrigin.Broadcast, DataOrigin.Demo, DataOrigin.Unknown })
        {
            var p = Enabled();
            p.OnDeviceState(State(In, In, origin), Playing, false, T0);
            Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out, origin), Playing, false, T0).Kind);
        }
    }

    [Fact]
    public void Missing_capability_session_or_side_means_no_action()
    {
        var p = Enabled();
        p.OnDeviceState(State(In, In, caps: DeviceCapabilities.None), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out, caps: DeviceCapabilities.None), Playing, false, T0).Kind);

        p = Enabled();
        p.OnDeviceState(State(In, In, session: false), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out, session: false), Playing, false, T0).Kind);

        p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, null), Playing, false, T0).Kind);
    }

    [Fact]
    public void A_gap_in_ear_data_breaks_the_transition()
    {
        // In ear, then unknown (e.g. transitional/disconnected), then out: not a defined removal.
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        p.OnDeviceState(State(In, PodPlacement.Disconnected), Playing, false, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out), Playing, false, T0).Kind);
    }

    [Fact]
    public void During_a_call_nothing_is_paused()
    {
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, true, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(Out, Out), Playing, true, T0).Kind);
    }

    [Fact]
    public void Putting_back_resumes_only_the_session_Clasp_paused()
    {
        var p = PausedByClasp();
        var action = p.OnDeviceState(State(In, Out, at: T0.AddMinutes(1)), Paused, false, T0.AddMinutes(1));
        Assert.Equal(EarMediaActionKind.Resume, action.Kind);
        Assert.Equal("spotify#1", action.SessionId);
        Assert.False(p.HasPendingResume);
    }

    [Fact]
    public void AnyRemoved_resumes_only_when_every_removed_bud_is_back()
    {
        var p = PausedByClasp(EarPauseTrigger.AnyRemoved); // 2 in -> 0 in
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, Out), Paused, false, T0).Kind);
    }

    [Fact]
    public void Unconfirmed_pause_is_never_resumed()
    {
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        var pause = p.OnDeviceState(State(Out, Out), Playing, false, T0);
        p.PauseIssued(pause.SessionId!);
        // Windows never reported the session as paused.
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
    }

    [Fact]
    public void Failed_pause_is_never_resumed()
    {
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        p.OnDeviceState(State(Out, Out), Playing, false, T0);
        p.PauseFailed();
        p.OnMediaChanged(Paused, T0); // a pause by someone else
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
    }

    [Fact]
    public void Manual_play_after_Clasp_paused_cancels_the_resume()
    {
        var p = PausedByClasp();
        p.OnMediaChanged(Playing, T0);
        p.OnMediaChanged(Paused, T0); // and paused again by hand
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
    }

    [Fact]
    public void Repeated_paused_reports_keep_the_claim()
    {
        var p = PausedByClasp();
        p.OnMediaChanged(Paused, T0);
        Assert.True(p.HasPendingResume);
    }

    [Fact]
    public void Player_change_cancels_the_resume()
    {
        var p = PausedByClasp();
        var other = new MediaSnapshot("browser#7", MediaPlayback.Paused);
        p.OnMediaChanged(other, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), other, false, T0).Kind);
    }

    [Fact]
    public void Session_mismatch_at_resume_time_is_refused()
    {
        // The claim survived (no change event seen), but the current session differs.
        var p = PausedByClasp();
        var action = p.OnDeviceState(State(In, In), new("browser#7", MediaPlayback.Paused), false, T0);
        Assert.Equal(EarMediaActionKind.None, action.Kind);
        Assert.False(p.HasPendingResume);
    }

    [Fact]
    public void A_call_cancels_the_resume()
    {
        var p = PausedByClasp();
        p.OnDeviceState(State(Out, Out), Paused, true, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
    }

    [Fact]
    public void Disconnect_cancels_the_resume()
    {
        var p = PausedByClasp();
        p.OnDeviceState(State(Out, Out, session: false), Paused, false, T0);
        p.OnDeviceState(State(Out, Out), Paused, false, T0); // reconnected, still out
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
    }

    [Fact]
    public void Device_change_cancels_the_resume()
    {
        var p = PausedByClasp();
        p.OnDeviceState(State(Out, Out, deviceId: "other"), Paused, false, T0);
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In, deviceId: "other"), Paused, false, T0).Kind);
    }

    [Fact]
    public void Resume_window_expires()
    {
        var p = PausedByClasp();
        var late = p.OnDeviceState(State(In, In, at: T0.AddMinutes(11)), Paused, false, T0.AddMinutes(11));
        Assert.Equal(EarMediaActionKind.None, late.Kind);
        Assert.Equal("resume window expired", late.Reason); // the window, not ear-report freshness
        Assert.False(p.HasPendingResume);
    }

    [Fact]
    public void Turning_the_setting_off_forgets_the_pause()
    {
        var p = PausedByClasp();
        p.Enabled = false;
        p.Enabled = true;
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
    }

    [Fact]
    public void A_paused_report_after_the_confirm_window_is_the_users_pause()
    {
        // Review EF-REVIEW: the player ignored Clasp's pause; the user paused minutes later.
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        var pause = p.OnDeviceState(State(Out, Out), Playing, false, T0);
        p.PauseIssued(pause.SessionId!);
        p.OnMediaChanged(Paused, T0.AddMinutes(3));
        Assert.False(p.HasPendingResume);
        var back = p.OnDeviceState(State(In, In, at: T0.AddMinutes(4)), Paused, false, T0.AddMinutes(4));
        Assert.Equal(EarMediaActionKind.None, back.Kind);
        Assert.Equal("Clasp did not pause anything", back.Reason);
    }

    [Fact]
    public void A_queued_pause_is_abandoned_when_the_buds_go_back_in_first()
    {
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        var pause = p.OnDeviceState(State(Out, Out), Playing, false, T0);
        Assert.True(p.IsPauseStillWanted(pause.SessionId!));
        p.OnDeviceState(State(In, In), Playing, false, T0); // back in before the pause was sent
        Assert.False(p.IsPauseStillWanted(pause.SessionId!));
        Assert.False(p.HasPendingResume);
    }

    [Fact]
    public void A_paused_report_before_Clasp_sent_its_pause_is_the_users_pause()
    {
        // EF-IR review F2, policy layer: the coordinator also drops such a claim at dispatch.
        var p = Enabled();
        p.OnDeviceState(State(In, In), Playing, false, T0);
        var pause = p.OnDeviceState(State(Out, Out), Playing, false, T0);
        p.OnMediaChanged(Paused, T0); // the user paused while Clasp's pause was still queued
        Assert.False(p.HasPendingResume);
        Assert.False(p.IsPauseStillWanted(pause.SessionId!));

        // Once the pause is being sent, the same report does confirm it.
        var q = Enabled();
        q.OnDeviceState(State(In, In), Playing, false, T0);
        var pause2 = q.OnDeviceState(State(Out, Out), Playing, false, T0);
        q.PauseSending(pause2.SessionId!);
        q.OnMediaChanged(Paused, T0);
        Assert.True(q.HasPendingResume);
    }

    [Fact]
    public void Resume_happens_at_most_once()
    {
        var p = PausedByClasp();
        Assert.Equal(EarMediaActionKind.Resume, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
        p.OnDeviceState(State(Out, Out), Paused, false, T0); // removed while paused: nothing playing
        Assert.Equal(EarMediaActionKind.None, p.OnDeviceState(State(In, In), Paused, false, T0).Kind);
    }
}
