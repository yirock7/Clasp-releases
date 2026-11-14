using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.State;

namespace AppleDeviceHub.Core.Tests;

public class ObservationAndBatteryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unreported_value_is_unknown_not_stale()
        => Assert.Equal(Freshness.Unknown, Observation<int>.Unknown.FreshnessAt(T0, TimeSpan.FromMinutes(5)));

    [Fact]
    public void Old_value_becomes_stale_and_recent_value_stays_fresh()
    {
        var old = Observation<int>.At(80, T0 - TimeSpan.FromMinutes(10));
        var recent = Observation<int>.At(80, T0 - TimeSpan.FromMinutes(1));
        Assert.Equal(Freshness.Stale, old.FreshnessAt(T0, TimeSpan.FromMinutes(5)));
        Assert.Equal(Freshness.Fresh, recent.FreshnessAt(T0, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(255)] // NTPods' "absent" sentinel
    [InlineData(101)]
    [InlineData(-1)]
    [InlineData(null)]
    public void Out_of_range_levels_are_unknown_and_drop_the_charging_flag(int? raw)
    {
        var c = BatteryComponent.From(raw, true, T0);
        Assert.False(c.Level.HasValue);
        Assert.False(c.Charging.HasValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Boundary_levels_are_kept(int raw)
        => Assert.Equal(raw, BatteryComponent.From(raw, false, T0).Level.Value);
}

public class LinkStateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static Observation<bool> B(bool? v) => v is bool b ? Observation<bool>.At(b, T0) : Observation<bool>.Unknown;

    [Theory]
    [InlineData(null, null, true, null, ConnectionSummary.Connected)]   // AAP session alone
    [InlineData(false, false, false, true, ConnectionSummary.Connected)] // Windows audio without AAP (no driver)
    [InlineData(true, true, false, false, ConnectionSummary.Connecting)]
    [InlineData(true, false, false, false, ConnectionSummary.Nearby)]   // BLE only: not "connected"
    [InlineData(false, false, false, false, ConnectionSummary.NotDetected)]
    [InlineData(null, null, null, null, ConnectionSummary.Unknown)]
    public void Summary_keeps_nearby_session_and_audio_distinct(
        bool? nearby, bool? connecting, bool? session, bool? audio, ConnectionSummary expected)
        => Assert.Equal(expected, new LinkState(B(nearby), B(connecting), B(session), B(audio)).Summarize());
}

public class DeviceStateStoreTests
{
    private static DeviceState State(string id, long seq) => new(
        id, "AirPods", "AirPods Pro 3", DataOrigin.Demo, seq, DateTimeOffset.UnixEpoch, DeviceCapabilities.None,
        LinkState.Unknown, BatteryState.Unknown, ListeningState.Unknown, default, default);

    [Fact]
    public void Ignores_states_for_a_device_the_user_did_not_select()
    {
        var store = new DeviceStateStore("mine");
        Assert.False(store.TryApply(State("stranger", 1), out _));
        Assert.Null(store.Current);
    }

    [Fact]
    public void Drops_duplicate_and_out_of_order_updates()
    {
        var store = new DeviceStateStore("mine");
        Assert.True(store.TryApply(State("mine", 2), out var prev1));
        Assert.Null(prev1);
        Assert.False(store.TryApply(State("mine", 2), out _)); // duplicate
        Assert.False(store.TryApply(State("mine", 1), out _)); // late arrival
        Assert.True(store.TryApply(State("mine", 3), out var prev3));
        Assert.Equal(2, prev3!.Sequence);
    }
}

public class RequestReconcilerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static readonly Guid Id = Guid.NewGuid();

    private static PendingRequest<ListeningMode> Pending(ListeningMode want = ListeningMode.Transparency)
        => RequestReconciler.Begin(Id, want, ListeningMode.NoiseCancellation, T0);

    private static CommandResult Result(CommandOutcome outcome, Guid? id = null)
        => new(id ?? Id, outcome, DataOrigin.Demo, outcome == CommandOutcome.Failed ? "boom" : null);

    [Fact]
    public void Acknowledgement_alone_does_not_confirm()
    {
        var r = RequestReconciler.OnCommandResult(Pending(), Result(CommandOutcome.Acknowledged));
        Assert.Equal(RequestStatus.Pending, r.Status);
        Assert.Equal(RequestStatus.Confirmed, RequestReconciler.OnActual(r, ListeningMode.Transparency).Status);
    }

    [Fact]
    public void Readback_may_arrive_before_the_result_and_still_wins()
    {
        var r = RequestReconciler.OnActual(Pending(), ListeningMode.Transparency);
        r = RequestReconciler.OnCommandResult(r, Result(CommandOutcome.Failed));
        Assert.Equal(RequestStatus.Confirmed, r.Status);
    }

    [Fact]
    public void Failure_is_reported_with_its_detail()
    {
        var r = RequestReconciler.OnCommandResult(Pending(), Result(CommandOutcome.Failed));
        Assert.Equal(RequestStatus.Failed, r.Status);
        Assert.Equal("boom", r.Detail);
    }

    [Fact]
    public void Result_for_an_older_command_is_ignored()
        => Assert.True(RequestReconciler.OnCommandResult(Pending(), Result(CommandOutcome.Failed, Guid.NewGuid())).IsPending);

    [Fact]
    public void Unchanged_readback_keeps_waiting()
        => Assert.True(RequestReconciler.OnActual(Pending(), ListeningMode.NoiseCancellation).IsPending);

    [Fact]
    public void Change_made_on_the_device_supersedes_the_request()
        => Assert.Equal(RequestStatus.Superseded, RequestReconciler.OnActual(Pending(), ListeningMode.Off).Status);

    [Fact]
    public void Missing_readback_times_out()
    {
        var r = Pending();
        Assert.True(RequestReconciler.OnTick(r, T0 + TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)).IsPending);
        Assert.Equal(RequestStatus.TimedOut, RequestReconciler.OnTick(r, T0 + TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)).Status);
    }

    [Fact]
    public void Readback_after_a_timeout_still_confirms()
    {
        var timedOut = RequestReconciler.OnTick(Pending(), T0 + TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));
        Assert.Equal(RequestStatus.Confirmed, RequestReconciler.OnActual(timedOut, ListeningMode.Transparency).Status);
        Assert.Equal(RequestStatus.TimedOut, RequestReconciler.OnActual(timedOut, ListeningMode.Off).Status);
    }

    [Fact]
    public void Rapid_clicks_do_not_mistake_our_own_earlier_command_for_an_external_change()
    {
        var first = Pending(ListeningMode.Transparency);
        var second = RequestReconciler.Begin(Guid.NewGuid(), ListeningMode.Adaptive, ListeningMode.Transparency, T0, first);

        Assert.Equal(ListeningMode.NoiseCancellation, second.ActualWhenRequested); // baseline inherited
        var afterFirstLands = RequestReconciler.OnActual(second, ListeningMode.Transparency);
        Assert.True(afterFirstLands.IsPending);
        Assert.Equal(RequestStatus.Confirmed, RequestReconciler.OnActual(afterFirstLands, ListeningMode.Adaptive).Status);
    }
}

public class TransportSelectionTests
{
    [Fact]
    public void Defaults_to_the_demo()
    {
        var choice = TransportSelection.FromArgs([]);
        Assert.Equal(TransportKind.Demo, choice.Kind);
        Assert.True(choice.IsAvailable);
    }

    [Fact]
    public void Refuses_the_hardware_engine_instead_of_launching_it()
    {
        var choice = TransportSelection.FromArgs(["--transport=ntpods"]);
        Assert.Equal(TransportKind.NtpodsDaemon, choice.Kind);
        Assert.False(choice.IsAvailable);
    }

    [Fact]
    public void Rejects_unknown_transports()
        => Assert.False(TransportSelection.FromArgs(["--transport=bluetooth"]).IsAvailable);
}
