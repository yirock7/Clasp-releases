using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Media;

namespace AppleDeviceHub.Core.Tests.Media;

public class EarOutputSwitchPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Pods = "pods-a2dp";
    private const string Speaker = "speaker";
    private const string Other = "headset";

    private static EarOutputSwitchPolicy Make(string speaker = Speaker)
    {
        var p = new EarOutputSwitchPolicy();
        p.Configure(speaker, [Pods], null);
        return p;
    }

    private static AudioOutputSnapshot Snap(string? console, bool podsAvailable = true, bool speakerAvailable = true)
        => new(
            [
                new AudioOutputEndpoint(Pods, "AirPods", podsAvailable, null),
                new AudioOutputEndpoint(Speaker, "Speakers", speakerAvailable, null),
                new AudioOutputEndpoint(Other, "Headset", true, null),
            ],
            new Dictionary<AudioOutputRole, string?>
            {
                [AudioOutputRole.Console] = console,
                [AudioOutputRole.Multimedia] = console,
                [AudioOutputRole.Communications] = console,
            });

    private static BroadcastEarResult Stable(PodPlacement a, PodPlacement b, DateTimeOffset at)
        => new(BroadcastEarVerdict.Stable, new BroadcastEarReading(a, b, at), "stable");

    private static readonly BroadcastEarResult RejectedResult = BroadcastEarResult.Rejected("identity unconfirmed");

    private const PodPlacement In = PodPlacement.InEar;
    private const PodPlacement Out = PodPlacement.OutOfEar;

    [Fact]
    public void Empty_endpoint_means_off()
    {
        var p = Make("");
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        Assert.Empty(p.OnEar(Stable(Out, Out, T0), Snap(Pods), T0));
    }

    [Fact]
    public void Both_out_switches_console_and_multimedia_but_never_communications()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        var requests = p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5));
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => { Assert.Equal(Speaker, r.EndpointId); Assert.Equal(Pods, r.ExpectedCurrentEndpointId); Assert.False(r.SwitchBack); });
        Assert.DoesNotContain(requests, r => r.Role == AudioOutputRole.Communications);
    }

    [Fact]
    public void One_bud_still_in_does_not_switch()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        Assert.Empty(p.OnEar(Stable(Out, In, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5)));
    }

    [Fact]
    public void First_reading_both_out_is_only_a_baseline_and_does_not_switch()
    {
        var p = Make();
        Assert.Empty(p.OnEar(Stable(Out, Out, T0), Snap(Pods), T0));
    }

    [Fact]
    public void Rejected_or_pending_evidence_never_switches()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        Assert.Empty(p.OnEar(RejectedResult, Snap(Pods), T0.AddSeconds(5)));
        Assert.Empty(p.OnEar(new(BroadcastEarVerdict.Pending, null, "waiting"), Snap(Pods), T0.AddSeconds(5)));
        // Rejection lost the worn baseline, so a later both-out alone is not enough.
        Assert.Empty(p.OnEar(Stable(Out, Out, T0.AddSeconds(9)), Snap(Pods), T0.AddSeconds(9)));
    }

    [Fact]
    public void Does_not_switch_when_the_default_is_not_the_airpods_or_speaker_missing()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Other), T0);
        Assert.Empty(p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Other), T0.AddSeconds(5)));

        var q = Make();
        q.OnEar(Stable(In, In, T0), Snap(Pods, speakerAvailable: false), T0);
        Assert.Empty(q.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods, speakerAvailable: false), T0.AddSeconds(5)));
    }

    [Fact]
    public void Switches_back_only_when_clasp_switched_and_default_is_still_the_speaker()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        var t1 = T0.AddSeconds(5);
        var out1 = p.OnEar(Stable(Out, Out, t1), Snap(Pods), t1);
        foreach (var r in out1) p.ReportResult(r.Role, applied: true);
        Assert.True(p.HasSwitched);

        var t2 = T0.AddSeconds(60);
        var back = p.OnEar(Stable(In, Out, t2), Snap(Speaker), t2);
        Assert.Equal(2, back.Count);
        Assert.All(back, r => { Assert.Equal(Pods, r.EndpointId); Assert.Equal(Speaker, r.ExpectedCurrentEndpointId); Assert.True(r.SwitchBack); });
        Assert.False(p.HasSwitched);

        // Acts once: another stable reading does nothing more.
        Assert.Empty(p.OnEar(Stable(In, In, t2.AddSeconds(5)), Snap(Pods), t2.AddSeconds(5)));
    }

    [Fact]
    public void Never_switches_back_when_clasp_did_not_switch()
    {
        var p = Make();
        // Output was on the speaker all along; the AirPods go out and back in.
        p.OnEar(Stable(In, In, T0), Snap(Speaker), T0);
        p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Speaker), T0.AddSeconds(5));
        Assert.Empty(p.OnEar(Stable(In, In, T0.AddSeconds(30)), Snap(Speaker), T0.AddSeconds(30)));
    }

    [Fact]
    public void Manual_choice_wins_before_the_buds_go_back_in()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        foreach (var r in p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5)))
            p.ReportResult(r.Role, applied: true);

        // The user picks another output by hand.
        p.Observe(Snap(Other));
        Assert.False(p.HasSwitched);
        Assert.Empty(p.OnEar(Stable(In, In, T0.AddSeconds(30)), Snap(Other), T0.AddSeconds(30)));
    }

    [Fact]
    public void Manual_choice_wins_even_if_it_is_detected_only_when_the_buds_go_back()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        foreach (var r in p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5)))
            p.ReportResult(r.Role, applied: true);
        Assert.Empty(p.OnEar(Stable(In, In, T0.AddSeconds(30)), Snap(Other), T0.AddSeconds(30)));
    }

    [Fact]
    public void User_returning_to_airpods_by_hand_clears_the_claim()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        foreach (var r in p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5)))
            p.ReportResult(r.Role, applied: true);
        p.Observe(Snap(Pods)); // chosen by hand while the buds are still out
        Assert.False(p.HasSwitched);
    }

    [Fact]
    public void Failed_write_drops_the_claim_and_a_pending_one_survives_the_unchanged_snapshot()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        var requests = p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5));
        p.Observe(Snap(Pods)); // write still in flight: not a manual choice
        Assert.True(p.HasSwitched);
        foreach (var r in requests) p.ReportResult(r.Role, applied: false);
        Assert.False(p.HasSwitched);
    }

    [Fact]
    public void Airpods_disconnecting_forgets_the_claim_and_leaves_the_speaker_alone()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        foreach (var r in p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5)))
            p.ReportResult(r.Role, applied: true);
        p.Observe(Snap(Speaker, podsAvailable: false));
        Assert.False(p.HasSwitched);
        Assert.Empty(p.OnEar(Stable(In, In, T0.AddSeconds(30)), Snap(Speaker, podsAvailable: false), T0.AddSeconds(30)));
    }

    [Fact]
    public void Switch_back_expires_after_the_window()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        foreach (var r in p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5)))
            p.ReportResult(r.Role, applied: true);
        var late = T0.AddHours(1);
        Assert.Empty(p.OnEar(Stable(In, In, late), Snap(Speaker), late));
    }

    [Fact]
    public void Works_with_restore_previous_output_without_fighting_it()
    {
        // Restore-previous-output is armed for the same AirPods. Clasp's switch to the speaker is a
        // change away from a still-active AirPods endpoint, which restoration treats as manual: it
        // issues no restore request and Clasp's claim stays intact.
        var restore = new AudioOutputRestorationPolicy();
        restore.Configure(true, Pods, null, null);
        var p = Make();

        var onPods = Snap(Pods);
        Assert.Empty(restore.Observe(Snap(Speaker))); // history: speaker was the earlier output
        Assert.Empty(restore.Observe(onPods));
        p.OnEar(Stable(In, In, T0), onPods, T0);
        foreach (var r in p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), onPods, T0.AddSeconds(5)))
            p.ReportResult(r.Role, applied: true);

        var onSpeaker = Snap(Speaker);
        Assert.Empty(restore.Observe(onSpeaker));
        p.Observe(onSpeaker);
        Assert.True(p.HasSwitched);
        Assert.False(restore.HasRememberedOutput);

        // Buds back in: Clasp returns to the AirPods; restoration then starts a fresh claim.
        var back = p.OnEar(Stable(In, In, T0.AddSeconds(30)), onSpeaker, T0.AddSeconds(30));
        Assert.All(back, r => Assert.Equal(Pods, r.EndpointId));
        Assert.Empty(restore.Observe(onPods));
    }

    [Fact]
    public void A_broadcast_alone_never_switches_the_sound_when_the_gate_says_the_airpods_are_not_connected()
    {
        // A spoofed broadcast is rejected by the gate (not connected in Windows / identity unconfirmed).
        // Rejected evidence loses the worn baseline, so a later "both out" cannot switch anything.
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Pods), T0);
        Assert.Empty(p.OnEar(BroadcastEarResult.Rejected("AirPods not connected in Windows"), Snap(Pods), T0.AddSeconds(1)));
        Assert.Empty(p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Pods), T0.AddSeconds(5)));
        Assert.False(p.HasSwitched);
    }

    [Fact]
    public void Nothing_switches_when_the_default_output_is_not_the_airpods()
    {
        var p = Make();
        p.OnEar(Stable(In, In, T0), Snap(Other), T0);
        Assert.Empty(p.OnEar(Stable(Out, Out, T0.AddSeconds(5)), Snap(Other), T0.AddSeconds(5)));
    }
}
