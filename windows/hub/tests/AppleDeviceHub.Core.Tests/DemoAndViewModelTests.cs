using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests;

public class DemoTransportTests
{
    [Fact]
    public async Task Case_open_story_reports_nearby_then_connecting_then_connected_all_marked_demo()
    {
        var states = new List<DeviceState>();
        await using var demo = new DemoAirPodsTransport(TestSupport.Instant());
        demo.StateChanged += (_, s) => { lock (states) states.Add(s); };

        await demo.StartAsync();

        Assert.Equal(
            new[] { ConnectionSummary.Nearby, ConnectionSummary.Connecting, ConnectionSummary.Connected },
            states.Select(s => s.Link.Summarize()));
        Assert.All(states, s => Assert.Equal(DataOrigin.Demo, s.Origin));
        Assert.Equal(states.Select(s => s.Sequence).OrderBy(x => x), states.Select(s => s.Sequence));
    }

    [Fact]
    public async Task Simulated_failure_is_labelled_demo_is_one_shot_and_changes_nothing()
    {
        var scenario = TestSupport.Instant();
        await using var demo = new DemoAirPodsTransport(scenario);
        await demo.StartAsync();
        var before = demo.Current!.Listening.Actual.Value;

        scenario.FailNextCommand = true;
        var result = await demo.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Off));

        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Contains("Demo", result.Detail);
        Assert.False(scenario.FailNextCommand);
        await Task.Delay(50); // give a (wrong) readback a chance to appear
        Assert.Equal(before, demo.Current!.Listening.Actual.Value);
    }

    [Fact]
    public async Task Successful_command_is_acknowledged_then_read_back()
    {
        await using var demo = new DemoAirPodsTransport(TestSupport.Instant());
        await demo.StartAsync();

        var result = await demo.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));

        Assert.Equal(CommandOutcome.Acknowledged, result.Outcome);
        await TestSupport.WaitUntil(() => demo.Current!.Listening.Actual.Value == ListeningMode.Transparency);
    }

    [Fact]
    public async Task Commands_are_refused_while_the_demo_buds_are_out_of_range()
    {
        await using var demo = new DemoAirPodsTransport(TestSupport.Instant());
        await demo.StartAsync();
        demo.SimulateOutOfRange();

        var result = await demo.SendAsync(new SetConversationAwarenessCommand(Guid.NewGuid(), false));
        Assert.Equal(CommandOutcome.TransportUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Out_of_range_strength_is_rejected()
    {
        await using var demo = new DemoAirPodsTransport(TestSupport.Instant());
        await demo.StartAsync();
        var result = await demo.SendAsync(new SetAdaptiveStrengthCommand(Guid.NewGuid(), 140));
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
    }
}

public class HubViewModelTests
{
    private static readonly object Gate = new();

    /// <summary>Serialises VM access the way the UI thread would.</summary>
    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    [Fact]
    public async Task Demo_view_model_marks_everything_as_demo()
    {
        await using var vm = new HubViewModel(new DemoAirPodsTransport(TestSupport.Instant()), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        await vm.StartAsync();

        Assert.True(vm.IsDemo);
        Assert.Equal("Connected", vm.ConnectionText);
        Assert.Equal("80%", vm.Left.PercentText);
        Assert.Contains("demo data", vm.Left.AutomationName);
        Assert.EndsWith("· Demo", vm.ModeStatusText);
    }

    [Fact]
    public async Task Demo_conversation_awareness_toggle_stays_enabled()
    {
        // The flyout binds the toggle's IsEnabled to this; hardware-only gating must not disable the demo.
        await using var vm = new HubViewModel(new DemoAirPodsTransport(TestSupport.Instant()), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        await vm.StartAsync();
        Assert.True(vm.IsConversationAwarenessInputEnabled);
        Assert.Equal("", vm.ConversationAwarenessReadOnlyText);
    }

    [Fact]
    public async Task Request_shows_pending_then_confirms_on_readback()
    {
        await using var vm = new HubViewModel(new DemoAirPodsTransport(TestSupport.Instant()), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        await vm.StartAsync();

        var task = vm.RequestModeAsync(ListeningMode.Transparency);
        Assert.Equal(ListeningMode.Transparency, vm.DisplayedMode);
        await task;

        await TestSupport.WaitUntil(() => vm.ActualMode == ListeningMode.Transparency && !vm.IsModePending);
        Assert.False(vm.HasModeError);
    }

    [Fact]
    public async Task Failure_restores_the_actual_mode_and_explains_why()
    {
        var scenario = TestSupport.Instant();
        await using var vm = new HubViewModel(new DemoAirPodsTransport(scenario), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        await vm.StartAsync();
        var before = vm.ActualMode;

        scenario.FailNextCommand = true;
        await vm.RequestModeAsync(ListeningMode.Off);

        Assert.Equal(before, vm.DisplayedMode);
        Assert.True(vm.HasModeError);
        Assert.Contains("Simulated failure", vm.LastErrorText);
        Assert.False(scenario.FailNextCommand); // the one-shot flag resets after the one-shot failure
    }

    [Fact]
    public async Task Unknown_battery_is_shown_as_unknown_never_as_a_number()
    {
        var scenario = TestSupport.Instant();
        scenario.Battery = DemoBatteryScenario.Unknown;
        await using var vm = new HubViewModel(new DemoAirPodsTransport(scenario), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        await vm.StartAsync();

        Assert.False(vm.Case.IsKnown);
        Assert.Equal("—", vm.Case.PercentText);
        Assert.Equal("Unknown", vm.Case.StatusText);
    }

    [Fact]
    public async Task Stale_battery_is_labelled_with_its_age()
    {
        var scenario = TestSupport.Instant();
        scenario.Battery = DemoBatteryScenario.Stale;
        await using var vm = new HubViewModel(new DemoAirPodsTransport(scenario), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        await vm.StartAsync();

        Assert.True(vm.Left.IsStale);
        Assert.Equal("12 min ago", vm.Left.StatusText);
    }

    [Fact]
    public async Task Missing_readback_times_out_and_falls_back_to_the_actual_mode()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow());

        await vm.RequestModeAsync(ListeningMode.Off); // acknowledged, never read back
        Assert.True(vm.IsModePending);

        time.Advance(HubViewModel.RequestTimeout);

        Assert.False(vm.IsModePending);
        Assert.True(vm.HasModeError);
        Assert.Equal(ListeningMode.NoiseCancellation, vm.DisplayedMode);
    }

    [Fact]
    public async Task Change_on_the_earbud_while_pending_wins()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow());

        await vm.RequestModeAsync(ListeningMode.Off);
        transport.Publish(ListeningMode.Transparency, time.GetUtcNow()); // stem press elsewhere

        Assert.False(vm.IsModePending);
        Assert.Equal(ListeningMode.Transparency, vm.DisplayedMode);
        Assert.Contains("changed on the device", vm.ModeStatusText);
    }

    [Fact]
    public async Task States_from_another_device_and_replayed_states_are_ignored()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport("mine");
        await using var vm = new HubViewModel(transport, "mine", Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow(), sequence: 5);

        transport.Publish(ListeningMode.Off, time.GetUtcNow(), deviceId: "stranger", sequence: 6);
        transport.Publish(ListeningMode.Off, time.GetUtcNow(), sequence: 5);

        Assert.Equal(ListeningMode.NoiseCancellation, vm.ActualMode);
    }

    [Fact]
    public async Task Late_readback_after_a_timeout_clears_the_error()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow());

        await vm.RequestModeAsync(ListeningMode.Off);
        time.Advance(HubViewModel.RequestTimeout);
        Assert.True(vm.HasModeError);

        transport.Publish(ListeningMode.Off, time.GetUtcNow()); // the change landed after all

        Assert.False(vm.HasModeError);
        Assert.Null(vm.LastErrorText);
        Assert.Equal(ListeningMode.Off, vm.DisplayedMode);
    }

    [Fact]
    public async Task Old_timeout_does_not_come_back_after_a_later_command_succeeds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow());

        await vm.RequestModeAsync(ListeningMode.Off);
        time.Advance(HubViewModel.RequestTimeout); // mode request times out

        await vm.SetConversationAwarenessAsync(true);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow(), awareness: true); // confirmed
        time.Advance(HubViewModel.RequestTimeout * 2);

        Assert.Null(vm.LastErrorText);
        Assert.True(vm.ConversationAwarenessOn);
    }

    [Fact]
    public async Task Unknown_settings_are_not_shown_as_off_or_zero()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);

        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: null, strength: null);

        Assert.False(vm.IsConversationAwarenessKnown);
        Assert.False(vm.IsAdaptiveStrengthKnown);
        Assert.False(vm.IsAdaptiveStrengthEnabled); // a slider resting at 0 would look reported
    }

    // ---- Unknown is never a value (packet 2.1, step 6) ------------------------------------
    // The view model itself must hold "no value" for an unreported setting, and everything a
    // person sees or hears must say it is unknown. Before this rule was enforced here, the view
    // model stored Off / 0 and relied on the XAML hiding the control.

    [Fact]
    public async Task Unreported_settings_hold_no_value_and_say_so_on_screen_and_to_Narrator()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);

        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: null, strength: null);

        Assert.Null(vm.ConversationAwarenessOn);
        Assert.Null(vm.AdaptiveStrength);
        Assert.Equal("Unknown", vm.ConversationAwarenessStateText);
        Assert.Equal("—", vm.AdaptiveStrengthText);
        Assert.Equal("Conversation Awareness, not reported yet", vm.ConversationAwarenessAutomationName);
        Assert.Equal("Adaptive strength, not reported yet", vm.AdaptiveStrengthAutomationName);
        HonestyGuard.AssertClaimsNoValue(vm.ConversationAwarenessStateText, vm.AdaptiveStrengthText,
            vm.ConversationAwarenessAutomationName, vm.AdaptiveStrengthAutomationName);
    }

    [Fact]
    public async Task A_setting_that_stops_being_reported_drops_its_old_value()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);

        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: true, strength: 40);
        Assert.True(vm.ConversationAwarenessOn);
        Assert.Equal(40, vm.AdaptiveStrength);
        Assert.Equal("On", vm.ConversationAwarenessStateText);
        Assert.Equal("40", vm.AdaptiveStrengthText);

        time.Advance(TimeSpan.FromSeconds(1));
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: null, strength: null);

        Assert.Null(vm.ConversationAwarenessOn);
        Assert.Null(vm.AdaptiveStrength);
        Assert.False(vm.IsConversationAwarenessKnown);
        Assert.False(vm.IsAdaptiveStrengthKnown);
        Assert.Equal("—", vm.AdaptiveStrengthText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Toggling_an_unreported_setting_sends_nothing(bool requested)
    {
        // A hidden ToggleSwitch still raises Toggled when its binding changes; with no reported
        // value there is nothing to change from, so no command may go out.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        int sends = 0;
        transport.OnSend = c => { sends++; return new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware); };
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: null, strength: null);

        await vm.SetConversationAwarenessAsync(requested);
        // Strength is also refused earlier because IsAdaptiveStrengthEnabled requires a reported
        // value; the explicit null guard in SetAdaptiveStrengthAsync is defence in depth.
        await vm.SetAdaptiveStrengthAsync(requested ? 80 : 0);

        Assert.Equal(0, sends);
        Assert.Null(vm.ConversationAwarenessOn);
        Assert.Null(vm.AdaptiveStrength);
    }

    [Fact]
    public async Task Queued_strength_is_dropped_when_the_device_stops_reporting_it()
    {
        // Review finding (packet 2.1): a value queued behind an in-flight command was still sent
        // after the setting became unknown.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var first = new TaskCompletionSource<CommandResult>();
        var sent = new List<DeviceCommand>();
        transport.OnSendAsync = c =>
        {
            sent.Add(c);
            return sent.Count == 1 ? first.Task : Task.FromResult(new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware));
        };
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);   // sent, held in flight
        await vm.SetAdaptiveStrengthAsync(70);            // queued behind it
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: null); // device stops reporting
        first.SetResult(new CommandResult(sent[0].CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware));
        await inFlight;

        Assert.Single(sent);
        Assert.Null(vm.AdaptiveStrength);
        Assert.Equal("—", vm.AdaptiveStrengthText);
    }

    // ---- Queued strength after the in-flight command ends (packet 2.2, finding R22-01) ----
    // A value queued behind an in-flight command is sent only while the in-flight request is
    // still Pending or Confirmed. Superseded (the device changed it), TimedOut and Failed end
    // the user's chain: the queued intent must not override the device or revive an error.

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Test helper; the view model under test owns and disposes the transport via await using.")]
    private sealed class HeldStrengthTransport
    {
        public readonly ScriptedTransport Transport = new();
        public readonly List<DeviceCommand> Sent = new();
        public readonly TaskCompletionSource<CommandResult> First = new();

        public HeldStrengthTransport()
        {
            Transport.OnSendAsync = c =>
            {
                Sent.Add(c);
                return Sent.Count == 1
                    ? First.Task
                    : Task.FromResult(new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware));
            };
        }

        /// <summary>Answer the held first command with its own (captured) command ID.</summary>
        public void AcknowledgeFirst()
            => First.SetResult(new CommandResult(Sent[0].CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware));
    }

    [Fact]
    public async Task Queued_strength_is_not_sent_after_the_device_changes_strength_itself()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var held = new HeldStrengthTransport();
        await using var vm = new HubViewModel(held.Transport, held.Transport.DeviceId, Dispatch, time: time);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);            // sent, answer held
        await vm.SetAdaptiveStrengthAsync(70);                     // queued, not sent
        Assert.Single(held.Sent);

        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 20); // changed on the device
        held.AcknowledgeFirst();                                   // matching ID, Acknowledged
        await inFlight;

        Assert.Single(held.Sent);                                  // 70 was not sent
        Assert.Equal(20, vm.AdaptiveStrength);                     // the device's value wins
        Assert.False(vm.IsAdaptiveStrengthPending);
        Assert.Null(vm.LastErrorText);
    }

    [Fact]
    public async Task Queued_strength_is_sent_once_when_nothing_else_changed()
    {
        // The normal coalescing path must keep working: 60 in flight, 70 queued, 60 acknowledged.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var held = new HeldStrengthTransport();
        await using var vm = new HubViewModel(held.Transport, held.Transport.DeviceId, Dispatch, time: time);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);
        await vm.SetAdaptiveStrengthAsync(70);
        held.AcknowledgeFirst();
        await inFlight;

        Assert.Equal(2, held.Sent.Count);
        Assert.Equal(70, Assert.IsType<SetAdaptiveStrengthCommand>(held.Sent[1]).Percent);
        Assert.Equal(70, vm.AdaptiveStrength);
        Assert.True(vm.IsAdaptiveStrengthPending);                 // awaiting the readback of 70
    }

    [Fact]
    public async Task Queued_strength_is_sent_after_the_first_value_is_confirmed()
    {
        // Readback of 60 arrives before its acknowledgement: 60 is Confirmed, and the user's newer
        // drag (70) is still their intent, so it goes out.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var held = new HeldStrengthTransport();
        await using var vm = new HubViewModel(held.Transport, held.Transport.DeviceId, Dispatch, time: time);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);
        await vm.SetAdaptiveStrengthAsync(70);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 60);
        held.AcknowledgeFirst();
        await inFlight;

        Assert.Equal(2, held.Sent.Count);
        Assert.Equal(70, Assert.IsType<SetAdaptiveStrengthCommand>(held.Sent[1]).Percent);
    }

    [Fact]
    public async Task Queued_strength_is_not_sent_after_the_first_request_timed_out()
    {
        // The display has already gone back to the device's value with "No confirmation"; a late
        // acknowledgement must not then send the queued value.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var held = new HeldStrengthTransport();
        await using var vm = new HubViewModel(held.Transport, held.Transport.DeviceId, Dispatch, time: time);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);
        await vm.SetAdaptiveStrengthAsync(70);
        time.Advance(HubViewModel.RequestTimeout);                 // 60 times out while still in flight
        held.AcknowledgeFirst();
        await inFlight;

        Assert.Single(held.Sent);
        Assert.Equal(40, vm.AdaptiveStrength);
        Assert.False(vm.IsAdaptiveStrengthPending);
    }

    [Fact]
    public async Task Queued_strength_is_not_sent_when_the_device_moves_after_confirming()
    {
        // Review P2-1 (packet 2.2): Confirmed is terminal, so a later device change used to go
        // unnoticed and the late acknowledgement still sent the queued value.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var held = new HeldStrengthTransport();
        await using var vm = new HubViewModel(held.Transport, held.Transport.DeviceId, Dispatch, time: time);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);
        await vm.SetAdaptiveStrengthAsync(70);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 60); // 60 confirmed
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 20); // then changed on the device
        held.AcknowledgeFirst();
        await inFlight;

        Assert.Single(held.Sent);
        Assert.Equal(20, vm.AdaptiveStrength);
    }

    [Fact]
    public async Task Queued_strength_stays_dropped_after_a_timeout_is_later_confirmed()
    {
        // Review P2-1: TimedOut, then a late readback turns it into "Confirmed after the timeout";
        // the user already saw the revert and message, so the queued value must not come back.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var held = new HeldStrengthTransport();
        await using var vm = new HubViewModel(held.Transport, held.Transport.DeviceId, Dispatch, time: time);
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);
        await vm.SetAdaptiveStrengthAsync(70);
        time.Advance(HubViewModel.RequestTimeout);                                       // 60 times out
        held.Transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 60); // late readback
        held.AcknowledgeFirst();
        await inFlight;

        Assert.Single(held.Sent);
        Assert.Equal(60, vm.AdaptiveStrength);
    }

    [Fact]
    public async Task Strength_reported_again_while_a_request_is_pending_is_shown_not_dashed()
    {
        // Review finding (packet 2.1): value -> unknown -> value while pending left "—" on screen.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var held = new TaskCompletionSource<CommandResult>();
        transport.OnSendAsync = _ => held.Task;
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40);

        var inFlight = vm.SetAdaptiveStrengthAsync(60);
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: null);
        Assert.Null(vm.AdaptiveStrength);

        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40); // baseline again, still pending
        Assert.Equal(60, vm.AdaptiveStrength);   // the pending request, as before the gap
        Assert.Equal("60", vm.AdaptiveStrengthText);
        Assert.True(vm.IsAdaptiveStrengthKnown);

        held.SetResult(new CommandResult(Guid.Empty, CommandOutcome.Acknowledged, DataOrigin.Hardware));
        await inFlight;
    }

    [Fact]
    public async Task Pending_awareness_becomes_unknown_when_the_device_stops_reporting_it()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var held = new TaskCompletionSource<CommandResult>();
        transport.OnSendAsync = _ => held.Task;
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: false);

        var inFlight = vm.SetConversationAwarenessAsync(true);
        Assert.True(vm.ConversationAwarenessOn);
        Assert.Equal("Conversation Awareness, waiting for confirmation", vm.ConversationAwarenessAutomationName);

        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: null);
        Assert.Null(vm.ConversationAwarenessOn);
        Assert.Equal("Unknown", vm.ConversationAwarenessStateText);
        Assert.Equal("Conversation Awareness, not reported yet", vm.ConversationAwarenessAutomationName);

        held.SetResult(new CommandResult(Guid.Empty, CommandOutcome.Acknowledged, DataOrigin.Hardware));
        await inFlight;
    }

    [Fact]
    public async Task Control_values_reset_when_a_setting_becomes_unknown()
    {
        // Review finding (packet 2.1): x:Bind skips a function binding whose argument is null,
        // so the flyout binds these non-nullable projections and the hidden controls really reset.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: true, strength: 40);
        Assert.True(vm.ConversationAwarenessToggleValue);
        Assert.Equal(40, vm.AdaptiveStrengthSliderValue);

        raised.Clear();
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), awareness: null, strength: null);
        Assert.False(vm.ConversationAwarenessToggleValue);
        Assert.Equal(0, vm.AdaptiveStrengthSliderValue);
        Assert.Contains(nameof(HubViewModel.ConversationAwarenessToggleValue), raised);
        Assert.Contains(nameof(HubViewModel.AdaptiveStrengthSliderValue), raised);
    }

    [Fact]
    public async Task Unknown_battery_is_read_to_Narrator_as_unknown_with_no_number()
    {
        var scenario = TestSupport.Instant();
        scenario.Battery = DemoBatteryScenario.Unknown;
        await using var vm = new HubViewModel(new DemoAirPodsTransport(scenario), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        await vm.StartAsync();

        foreach (var item in new[] { vm.Left, vm.Right, vm.Case })
        {
            Assert.False(item.IsKnown);
            Assert.Equal(0, item.Level01);
            Assert.Contains("battery unknown", item.AutomationName);
            HonestyGuard.AssertClaimsNoValue(item.PercentText, item.StatusText, item.AutomationName);
            Assert.DoesNotContain("percent", item.AutomationName);
        }
    }

    [Fact]
    public async Task Echoed_toggle_value_does_not_send_a_second_command()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        int sends = 0;
        transport.OnSend = c => { sends++; return new CommandResult(c.CommandId, CommandOutcome.Failed, DataOrigin.Hardware, "nope"); };
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow());

        await vm.SetConversationAwarenessAsync(true); // fails, display reverts to false
        await vm.SetConversationAwarenessAsync(false); // the view echoing the reverted value back

        Assert.Equal(1, sends);
        Assert.False(vm.ConversationAwarenessOn);
    }
}
