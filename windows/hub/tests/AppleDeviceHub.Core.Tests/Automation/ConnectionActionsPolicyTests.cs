using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Connection;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Automation;

public sealed class ConnectionActionsPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private const string Endpoint = "{0.0.0.00000000}.{airpods}";
    private static readonly ConnectionActionSettings AllOn = new(true, true, 25, true);

    private static ConnectionObservation Off() => new(false, null, false, false);
    private static ConnectionObservation OnNoEndpoint() => new(true, null, false, false);
    private static ConnectionObservation On(bool isDefault = false) => new(true, Endpoint, true, isDefault);
    private static ConnectionActionContext Ctx(bool playing = false, bool restoring = false, DateTimeOffset? manual = null)
        => new(false, playing, restoring, manual);

    private static ConnectionActionsPolicy Baseline(bool connected)
    {
        var policy = new ConnectionActionsPolicy();
        var plan = policy.Evaluate(connected ? On(isDefault: true) : Off(), AllOn, new(true, false), At(0));
        Assert.Empty(plan.Actions);
        return policy;
    }

    private static ConnectionActionKind[] Kinds(ConnectionActionPlan plan) => plan.Actions.Select(a => a.Kind).ToArray();

    [Fact]
    public void AppStart_AlreadyConnected_DoesNothingEvenOnRepeatedObservations()
    {
        var policy = Baseline(connected: true);
        Assert.Empty(policy.Evaluate(On(true), AllOn, Ctx(), At(1)).Actions);
        Assert.Empty(policy.Evaluate(On(true), AllOn, Ctx(), At(60)).Actions);
    }

    [Fact]
    public void FirstObservationWithoutAppStartFlagIsStillABaseline()
    {
        var policy = new ConnectionActionsPolicy();
        Assert.Empty(policy.Evaluate(On(), AllOn, Ctx(), At(0)).Actions);
    }

    [Fact]
    public void ConnectAfterDisconnectedStart_RunsSwitchVolumePlayInOrder()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), AllOn, Ctx(), At(10));

        Assert.Equal([ConnectionActionKind.SwitchOutput, ConnectionActionKind.SetVolume, ConnectionActionKind.Play], Kinds(plan));
        Assert.Equal(Endpoint, plan.Actions[0].EndpointId);
        Assert.Equal(Endpoint, plan.Actions[1].EndpointId);
        Assert.Equal(25, plan.Actions[1].VolumePercent);
        Assert.True(plan.Actions[2].Delay > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, plan.Actions[0].Delay);
    }

    [Fact]
    public void ConnectActionsFireExactlyOncePerTransition()
    {
        var policy = Baseline(connected: false);
        Assert.NotEmpty(policy.Evaluate(On(), AllOn, Ctx(), At(10)).Actions);
        Assert.Empty(policy.Evaluate(On(true), AllOn, Ctx(), At(11)).Actions);
        Assert.Empty(policy.Evaluate(On(true), AllOn, Ctx(), At(30)).Actions);
    }

    [Fact]
    public void Disconnect_WhilePlaying_PausesOnce()
    {
        var policy = Baseline(connected: true);
        var plan = policy.Evaluate(Off(), AllOn, Ctx(playing: true), At(10));
        Assert.Equal([ConnectionActionKind.Pause], Kinds(plan));
        Assert.Empty(policy.Evaluate(Off(), AllOn, Ctx(playing: true), At(11)).Actions);
    }

    [Fact]
    public void Disconnect_NothingPlaying_DoesNotPause()
    {
        var policy = Baseline(connected: true);
        Assert.Empty(policy.Evaluate(Off(), AllOn, Ctx(playing: false), At(10)).Actions);
    }

    [Fact]
    public void Flap_ReconnectWithinWindow_SuppressesConnectActions()
    {
        var policy = Baseline(connected: true);
        Assert.Equal([ConnectionActionKind.Pause], Kinds(policy.Evaluate(Off(), AllOn, Ctx(playing: true), At(10))));
        var plan = policy.Evaluate(On(), AllOn, Ctx(), At(12.9));
        Assert.Empty(plan.Actions);
        Assert.Contains("flap", plan.Reason);
        // The flap connect is consumed: no late actions for it.
        Assert.Empty(policy.Evaluate(On(), AllOn, Ctx(), At(14)).Actions);
    }

    private static ConnectionActionContext WithOrigin(ConnectionOrigin origin) => new(false, false, Origin: origin);

    [Theory]
    [InlineData(ConnectionOrigin.None, false)]
    [InlineData(ConnectionOrigin.User, true)]
    [InlineData(ConnectionOrigin.Hotkey, true)]
    [InlineData(ConnectionOrigin.ExternalApproved, true)]
    [InlineData(ConnectionOrigin.NearbyAuto, false)]
    public void Origin_maps_to_a_user_requested_connect_only_when_a_person_asked(ConnectionOrigin origin, bool expected)
        => Assert.Equal(expected, WithOrigin(origin).UserRequestedConnect);

    [Fact]
    public void NearbyAuto_with_switch_on_connect_plans_only_the_output_switch()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), AllOn, WithOrigin(ConnectionOrigin.NearbyAuto), At(10));
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(plan));
        Assert.Equal(Endpoint, plan.Actions[0].EndpointId);
        Assert.DoesNotContain(ConnectionActionKind.Play, Kinds(plan));
        Assert.DoesNotContain(ConnectionActionKind.SetVolume, Kinds(plan));
    }

    [Fact]
    public void NearbyAuto_without_switch_on_connect_plans_nothing_even_with_play_and_volume_on()
    {
        var policy = Baseline(connected: false);
        var settings = new ConnectionActionSettings(true, true, 25, false);
        Assert.Empty(policy.Evaluate(On(isDefault: true), settings, WithOrigin(ConnectionOrigin.NearbyAuto), At(10)).Actions);
    }

    [Fact]
    public void NearbyAuto_never_plans_play_or_volume_when_the_output_is_already_the_default()
    {
        var policy = Baseline(connected: false);
        Assert.Empty(policy.Evaluate(On(isDefault: true), AllOn, WithOrigin(ConnectionOrigin.NearbyAuto), At(10)).Actions);
    }

    [Fact]
    public void NearbyAuto_output_switch_still_respects_a_recent_manual_choice()
    {
        var policy = Baseline(connected: false);
        var ctx = new ConnectionActionContext(false, false, LastManualOutputChoiceAt: At(5), Origin: ConnectionOrigin.NearbyAuto);
        Assert.Empty(policy.Evaluate(On(), AllOn, ctx, At(10)).Actions);
    }

    [Fact]
    public void NearbyAuto_waits_for_the_endpoint_then_switches_once_without_play_or_volume()
    {
        var policy = Baseline(connected: false);
        var waiting = policy.Evaluate(OnNoEndpoint(), AllOn, WithOrigin(ConnectionOrigin.NearbyAuto), At(10));
        Assert.Empty(waiting.Actions);
        Assert.NotNull(waiting.RecheckAt);
        var plan = policy.Evaluate(On(), AllOn, WithOrigin(ConnectionOrigin.NearbyAuto), At(11));
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(plan));
        Assert.Empty(policy.Evaluate(On(), AllOn, Ctx(), At(12)).Actions); // exactly once
    }

    [Theory]
    [InlineData(ConnectionOrigin.User)]
    [InlineData(ConnectionOrigin.Hotkey)]
    [InlineData(ConnectionOrigin.ExternalApproved)]
    public void Approved_or_personal_origins_still_switch_output(ConnectionOrigin origin)
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), SwitchOff, WithOrigin(origin), At(10));
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(plan));
    }

    [Fact]
    public void Connect_with_no_origin_follows_the_setting_and_does_not_force_a_switch()
    {
        var policy = Baseline(connected: false);
        Assert.Empty(policy.Evaluate(On(), SwitchOff, WithOrigin(ConnectionOrigin.None), At(10)).Actions);
    }

    [Fact]
    public void Play_carries_the_endpoint_it_was_planned_for()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), AllOn, Ctx(), At(10));
        Assert.Equal(Endpoint, plan.Actions.Single(a => a.Kind == ConnectionActionKind.Play).EndpointId);
    }

    private static readonly ConnectionActionSettings SwitchOff = new(false, false, null, false);
    private static ConnectionActionContext UserConnect(DateTimeOffset? manual = null, bool restoring = false)
        => new(false, false, restoring, manual, Origin: ConnectionOrigin.User);

    [Fact]
    public void UserConnect_SwitchesOutput_EvenWithTheSettingOff()
    {
        var policy = new ConnectionActionsPolicy();
        policy.Evaluate(Off(), SwitchOff, new(true, false), At(0));
        Assert.Empty(policy.Evaluate(On(), SwitchOff, Ctx(), At(10)).Actions); // an automatic connect follows the setting

        policy = new ConnectionActionsPolicy();
        policy.Evaluate(Off(), SwitchOff, new(true, false), At(0));
        var plan = policy.Evaluate(On(), SwitchOff, UserConnect(), At(10));
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(plan));
        Assert.Equal(Endpoint, plan.Actions[0].EndpointId);
    }

    [Fact]
    public void UserConnect_OverridesARecentManualChoiceAndARestore()
    {
        var policy = Baseline(connected: false);
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(policy.Evaluate(On(), SwitchOff, UserConnect(manual: At(5)), At(10))));

        policy = Baseline(connected: false);
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(policy.Evaluate(On(), SwitchOff, UserConnect(restoring: true), At(10))));
    }

    [Fact]
    public void UserConnect_RightAfterADisconnect_IsNotAFlap()
    {
        var policy = Baseline(connected: true);
        policy.Evaluate(Off(), SwitchOff, Ctx(), At(10));
        var plan = policy.Evaluate(On(), SwitchOff, UserConnect(), At(11));
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(plan));
    }

    [Fact]
    public void UserConnect_WaitsForTheEndpointLikeAnyConnect()
    {
        var policy = Baseline(connected: false);
        var waiting = policy.Evaluate(OnNoEndpoint(), SwitchOff, UserConnect(), At(10));
        Assert.Empty(waiting.Actions);
        Assert.NotNull(waiting.RecheckAt);
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(policy.Evaluate(On(), SwitchOff, UserConnect(), At(12))));
    }

    [Fact]
    public void Flap_ReconnectAtExactlyWindow_IsARealConnect()
    {
        var policy = Baseline(connected: true);
        policy.Evaluate(Off(), AllOn, Ctx(playing: true), At(10));
        var plan = policy.Evaluate(On(), AllOn, Ctx(), At(13));
        Assert.Contains(ConnectionActionKind.Play, Kinds(plan));
    }

    [Fact]
    public void Flap_DisconnectSoonAfterConnect_StillPauses()
    {
        var policy = Baseline(connected: false);
        Assert.NotEmpty(policy.Evaluate(On(), AllOn, Ctx(), At(10)).Actions);
        Assert.Equal([ConnectionActionKind.Pause], Kinds(policy.Evaluate(Off(), AllOn, Ctx(playing: true), At(11))));
    }

    [Fact]
    public void ConnectWithoutEndpoint_WaitsThenActsWhenItAppears()
    {
        var policy = Baseline(connected: false);
        var waiting = policy.Evaluate(OnNoEndpoint(), AllOn, Ctx(), At(10));
        Assert.Empty(waiting.Actions);
        Assert.Equal(At(25), waiting.RecheckAt);

        var plan = policy.Evaluate(On(), AllOn, Ctx(), At(12));
        Assert.Equal([ConnectionActionKind.SwitchOutput, ConnectionActionKind.SetVolume, ConnectionActionKind.Play], Kinds(plan));
    }

    [Fact]
    public void ConnectWithoutEndpoint_GivesUpAfterTimeout()
    {
        var policy = Baseline(connected: false);
        policy.Evaluate(OnNoEndpoint(), AllOn, Ctx(), At(10));
        var plan = policy.Evaluate(OnNoEndpoint(), AllOn, Ctx(), At(25));
        Assert.Empty(plan.Actions);
        Assert.Null(plan.RecheckAt);
        // A late endpoint no longer triggers anything for that connect.
        Assert.Empty(policy.Evaluate(On(), AllOn, Ctx(), At(30)).Actions);
    }

    [Fact]
    public void ConnectedButEndpointUnavailable_IsTreatedAsUnknownEndpoint()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(new ConnectionObservation(true, Endpoint, false, false), AllOn, Ctx(), At(10));
        Assert.Empty(plan.Actions);
        Assert.NotNull(plan.RecheckAt);
    }

    [Fact]
    public void DisconnectWhileWaitingForEndpoint_CancelsPendingConnect()
    {
        var policy = Baseline(connected: false);
        policy.Evaluate(OnNoEndpoint(), AllOn, Ctx(), At(10));
        policy.Evaluate(Off(), AllOn, Ctx(), At(11));
        // Reconnect within the flap window of that disconnect is suppressed; later ones are real.
        Assert.Empty(policy.Evaluate(On(), AllOn, Ctx(), At(12)).Actions);
    }

    [Fact]
    public void AllSettingsOff_NeverActs()
    {
        var policy = Baseline(connected: false);
        Assert.Empty(policy.Evaluate(On(), ConnectionActionSettings.AllOff, Ctx(), At(10)).Actions);
        Assert.Empty(policy.Evaluate(Off(), ConnectionActionSettings.AllOff, Ctx(playing: true), At(20)).Actions);
    }

    [Fact]
    public void OnlyAutoPlay_WhenAirPodsAlreadyDefault_PlaysWithoutSwitch()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(isDefault: true), new(true, false, null, false), Ctx(), At(10));
        Assert.Equal([ConnectionActionKind.Play], Kinds(plan));
    }

    [Fact]
    public void OnlyAutoPlay_WhenAirPodsNotDefault_SkipsPlayBecauseAudioWouldGoElsewhere()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(isDefault: false), new(true, false, null, false), Ctx(), At(10));
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void OnlyVolume_SetsVolumeOnly()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), new(false, false, 40, false), Ctx(), At(10));
        Assert.Equal([ConnectionActionKind.SetVolume], Kinds(plan));
        Assert.Equal(40, plan.Actions[0].VolumePercent);
    }

    [Fact]
    public void OnlySwitch_SwitchesOnlyAndSkipsWhenAlreadyDefault()
    {
        var settings = new ConnectionActionSettings(false, false, null, true);
        var policy = Baseline(connected: false);
        Assert.Equal([ConnectionActionKind.SwitchOutput], Kinds(policy.Evaluate(On(), settings, Ctx(), At(10))));

        var second = Baseline(connected: false);
        Assert.Empty(second.Evaluate(On(isDefault: true), settings, Ctx(), At(10)).Actions);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void VolumeOutsideZeroToHundred_IsIgnored(int percent, bool expected)
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(true), new(false, false, percent, false), Ctx(), At(10));
        Assert.Equal(expected, plan.Actions.Any(a => a.Kind == ConnectionActionKind.SetVolume));
    }

    [Fact]
    public void AutoPlay_SkippedWhenMediaAlreadyPlaying()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), AllOn, Ctx(playing: true), At(10));
        Assert.DoesNotContain(ConnectionActionKind.Play, Kinds(plan));
        Assert.Contains(ConnectionActionKind.SwitchOutput, Kinds(plan));
    }

    [Fact]
    public void RecentManualOutputChoice_BlocksSwitchAndPlayButNotVolume()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), AllOn, Ctx(manual: At(-5)), At(10));
        Assert.Equal([ConnectionActionKind.SetVolume], Kinds(plan));
    }

    [Fact]
    public void OldManualOutputChoice_NoLongerBlocks()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), AllOn, Ctx(manual: At(-25)), At(10)); // 35 s ago
        Assert.Contains(ConnectionActionKind.SwitchOutput, Kinds(plan));
    }

    [Fact]
    public void RestorationInProgress_BlocksSwitchAndPlay()
    {
        var policy = Baseline(connected: false);
        var plan = policy.Evaluate(On(), AllOn, Ctx(restoring: true), At(10));
        Assert.Equal([ConnectionActionKind.SetVolume], Kinds(plan));
        Assert.Contains("restore", plan.Reason);
    }

    [Fact]
    public void RestorationInProgress_DoesNotBlockPauseOnDisconnect()
    {
        var policy = Baseline(connected: true);
        var plan = policy.Evaluate(Off(), AllOn, Ctx(playing: true, restoring: true), At(10));
        Assert.Equal([ConnectionActionKind.Pause], Kinds(plan));
    }

    [Fact]
    public void ManualChoiceDuringWait_IsHonoredWhenEndpointFinallyAppears()
    {
        var policy = Baseline(connected: false);
        policy.Evaluate(OnNoEndpoint(), AllOn, Ctx(), At(10));
        var plan = policy.Evaluate(On(), AllOn, Ctx(manual: At(11)), At(12));
        Assert.DoesNotContain(ConnectionActionKind.SwitchOutput, Kinds(plan));
    }

    [Fact]
    public void Reset_MakesNextObservationABaselineAgain()
    {
        var policy = Baseline(connected: false);
        policy.Reset();
        Assert.Empty(policy.Evaluate(On(), AllOn, Ctx(), At(10)).Actions);
    }

    [Fact]
    public void AppStartFlagMidSession_ReBaselines()
    {
        var policy = Baseline(connected: false);
        Assert.Empty(policy.Evaluate(On(), AllOn, new(true, false), At(10)).Actions);
        Assert.Empty(policy.Evaluate(On(), AllOn, Ctx(), At(11)).Actions);
    }

    // ---- ConnectionObservation.From ----

    private const string Container = "11111111-2222-3333-4444-555555555555";

    private static ConnectionSnapshot Snapshot(bool complete, bool connected, params ConnectionAudioEndpoint[] endpoints)
        => new(complete,
            [new ConnectionDevice("dev1", "AirPods", Container, endpoints.Length > 0, connected, false, true, null)],
            endpoints, null);

    [Fact]
    public void From_IncompleteSnapshotOrUnknownDevice_IsNull()
    {
        Assert.Null(ConnectionObservation.From(Snapshot(false, true), "dev1"));
        Assert.Null(ConnectionObservation.From(Snapshot(true, true), "other"));
        Assert.Null(ConnectionObservation.From(Snapshot(true, true), ""));
    }

    [Fact]
    public void From_MatchesEndpointByContainerNotName()
    {
        var snapshot = Snapshot(true, true,
            new ConnectionAudioEndpoint("speakers", "AirPods", "99999999-2222-3333-4444-555555555555", true, true, false),
            new ConnectionAudioEndpoint("pods", "Headphones", Container.ToUpperInvariant(), true, false, true));
        var observation = ConnectionObservation.From(snapshot, "dev1");
        Assert.NotNull(observation);
        Assert.True(observation!.Connected);
        Assert.Equal("pods", observation.AudioEndpointId);
        Assert.True(observation.EndpointAvailable);
        Assert.False(observation.EndpointIsDefault);
    }

    [Fact]
    public void From_PrefersCurrentOutputAndIgnoresUnavailableEndpoints()
    {
        var snapshot = Snapshot(true, true,
            new ConnectionAudioEndpoint("a", "A", Container, true, false, true),
            new ConnectionAudioEndpoint("b", "B", Container, true, true, true),
            new ConnectionAudioEndpoint("c", "C", Container, false, false, true));
        var observation = ConnectionObservation.From(snapshot, "dev1")!;
        Assert.Equal("b", observation.AudioEndpointId);
        Assert.True(observation.EndpointIsDefault);

        var none = ConnectionObservation.From(
            Snapshot(true, true, new ConnectionAudioEndpoint("c", "C", Container, false, false, true)), "dev1")!;
        Assert.Null(none.AudioEndpointId);
        Assert.False(none.HasUsableEndpoint);
    }
}
