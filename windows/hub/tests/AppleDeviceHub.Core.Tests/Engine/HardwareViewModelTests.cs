using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Engine;

/// <summary>
/// The view model with a ControlSessionOnly hardware transport: "connected" means the AirPods
/// control session, Windows audio is a separate fact, and every unavailable control says why.
/// </summary>
public class HardwareViewModelTests
{
    private static readonly object Gate = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    private static (ScriptedHardwareTransport, HubViewModel, FakeTimeProvider) Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedHardwareTransport();
        var vm = new HubViewModel(transport, ScriptedHardwareTransport.Id, Dispatch, time: time);
        return (transport, vm, time);
    }

    [Fact]
    public async Task A_Bluetooth_link_alone_is_not_a_connected_control_session()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.Publish(nearby: true, session: false, audio: true, time.GetUtcNow());

        Assert.False(vm.IsConnected);
        Assert.Equal("Controls not connected", vm.ConnectionHeadline);
        Assert.Equal($"Bluetooth: connected, checked {HubViewModel.FormatClock(time.GetUtcNow())}", vm.BluetoothLinkText);
        Assert.Equal("Start session", vm.ConnectActionText);
        Assert.True(vm.IsConnectActionEnabled);
        Assert.Equal("Turns on your AirPods controls. Pairing and sound settings stay the same.", vm.ConnectionHint);
        Assert.Equal("Ready", vm.ConnectionText);
        Assert.False(vm.IsDemo);
        Assert.Equal("Hardware: Clasp engine, observe session", vm.DataSourceText);
    }

    [Fact]
    public async Task An_open_control_session_is_connected_and_offers_End_session_never_Disconnect()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.Publish(nearby: true, session: true, audio: false, time.GetUtcNow());

        Assert.True(vm.IsConnected);
        Assert.Equal("Controls connected", vm.ConnectionHeadline);
        Assert.Equal($"Bluetooth: not connected, checked {HubViewModel.FormatClock(time.GetUtcNow())}", vm.BluetoothLinkText);
        Assert.Equal("End session", vm.ConnectActionText);
        Assert.DoesNotContain("Disconnect", vm.ConnectActionText);
        Assert.Equal("Controls connected", vm.ConnectionText);

        t.Publish(nearby: true, session: true, audio: null, time.GetUtcNow());
        Assert.Equal("Bluetooth: unknown", vm.BluetoothLinkText);
    }

    [Fact]
    public async Task Start_and_End_session_send_connect_and_disconnect()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        var sent = new List<DeviceCommand>();
        t.OnSend = c =>
        {
            sent.Add(c);
            return new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware);
        };
        t.Publish(nearby: true, session: false, audio: true, time.GetUtcNow());

        await vm.ConnectAsync();
        Assert.IsType<ConnectCommand>(Assert.Single(sent));
        Assert.Equal("Starting session…", vm.ConnectionHeadline);
        Assert.Equal("Turning on your AirPods controls…", vm.ConnectionHint);

        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow());
        Assert.True(vm.IsConnected);

        await vm.DisconnectAsync();
        Assert.IsType<DisconnectCommand>(sent[^1]);
        Assert.Equal("Ending session…", vm.ConnectionHeadline);
        t.Publish(nearby: true, session: false, audio: true, time.GetUtcNow());
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsDisconnectPending);
        Assert.Equal("Start session", vm.ConnectActionText);
    }

    [Fact]
    public async Task Observe_session_selector_is_read_only_with_the_reason()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), writable: Array.Empty<ListeningMode>(), mode: ListeningMode.Transparency);

        Assert.True(vm.AreControlsAvailable);
        Assert.Equal(ListeningMode.Transparency, vm.ActualMode);
        Assert.False(vm.IsModeSupported(ListeningMode.Off));
        Assert.Equal("Observation session: changes disabled", vm.ModeReadOnlyText);

        int sends = 0;
        t.OnSend = c => { sends++; return new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware); };
        await vm.RequestModeAsync(ListeningMode.Off);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Control_session_with_writable_modes_has_no_read_only_reason()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.ReadOnlyReason = null;
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(),
            writable: new[] { ListeningMode.Transparency, ListeningMode.NoiseCancellation }, mode: ListeningMode.Off);

        Assert.Equal("", vm.ModeReadOnlyText);
        Assert.True(vm.IsModeSupported(ListeningMode.Transparency));
        Assert.False(vm.IsModeSupported(ListeningMode.Adaptive));
    }

    [Fact]
    public async Task Awareness_and_strength_say_they_are_not_in_this_build()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), mode: ListeningMode.Adaptive);

        Assert.False(vm.IsConversationAwarenessSupported);
        Assert.Equal("Not available yet", vm.ConversationAwarenessCaption);
        Assert.Equal("Unavailable", vm.ConversationAwarenessStateText);
        Assert.Equal("Conversation Awareness, not available yet", vm.ConversationAwarenessAutomationName);
        Assert.Null(vm.ConversationAwarenessOn);
        Assert.Equal("Not available yet", vm.AdaptiveStrengthCaption);
        Assert.Equal("Adaptive strength, not available yet", vm.AdaptiveStrengthAutomationName);
        Assert.False(vm.IsAdaptiveStrengthEnabled);
        Assert.Null(vm.AdaptiveStrength);
        HonestyGuard.AssertClaimsNoValue(vm.ConversationAwarenessStateText, vm.ConversationAwarenessAutomationName, vm.AdaptiveStrengthAutomationName);
    }

    [Fact]
    public async Task The_unavailable_reason_is_shown_and_tracks_the_transport()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        Assert.False(vm.HasUnavailableReason);

        t.SetReason("Driver interface not found for this device.");
        Assert.True(vm.HasUnavailableReason);
        Assert.Equal("Driver interface not found for this device.", vm.UnavailableReason);
        Assert.Equal("Not available right now", vm.ConnectionHeadline);
        Assert.Equal("Start session", vm.ConnectActionText);
        Assert.False(vm.IsConnectActionEnabled);

        t.Publish(nearby: false, session: null, audio: null, time.GetUtcNow());
        Assert.Equal("Controls not connected", vm.ConnectionHeadline);
        Assert.Equal("", vm.ConnectionHint); // the reason line says it instead
        Assert.False(vm.IsConnectActionEnabled);

        t.SetReason(null);
        Assert.False(vm.HasUnavailableReason);
    }

    [Fact]
    public async Task An_unusable_hardware_request_shows_the_reason_and_no_data()
    {
        await using var vm = new HubViewModel(new UnavailableHardwareTransport("--device is required."), "dev-unselected", Dispatch);
        await vm.StartAsync();

        Assert.False(vm.IsDemo);
        Assert.Null(vm.Demo);
        Assert.Equal("--device is required.", vm.UnavailableReason);
        Assert.Equal("Not available right now", vm.ConnectionHeadline);
        Assert.Equal("Not available right now", vm.ConnectionText);
        Assert.False(vm.IsConnectActionEnabled);
        HonestyGuard.AssertClaimsNoValue(vm.Left.PercentText, vm.Right.PercentText, vm.Case.PercentText); // no battery value
        Assert.Null(vm.ActualMode);
        Assert.Equal("Hardware: Clasp engine (not connected)", vm.DataSourceText);
    }

    [Fact]
    public async Task The_Bluetooth_link_line_carries_its_check_time_and_goes_stale()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        var firstCheck = time.GetUtcNow();
        t.Publish(nearby: true, session: false, audio: true, firstCheck);
        Assert.Equal($"Bluetooth: connected, checked {HubViewModel.FormatClock(firstCheck)}", vm.BluetoothLinkText);

        time.Advance(HubViewModel.LinkCheckMaxAge + TimeSpan.FromSeconds(1));
        vm.Refresh();
        Assert.Equal($"Bluetooth: connected, last checked {HubViewModel.FormatClock(firstCheck)}", vm.BluetoothLinkText);

        // A later devices reply: new value, new check time, fresh again.
        time.Advance(TimeSpan.FromMinutes(5));
        var secondCheck = time.GetUtcNow();
        t.Publish(nearby: true, session: false, audio: false, secondCheck);
        Assert.Equal($"Bluetooth: not connected, checked {HubViewModel.FormatClock(secondCheck)}", vm.BluetoothLinkText);
        Assert.NotEqual(HubViewModel.FormatClock(firstCheck), HubViewModel.FormatClock(secondCheck));
    }

    [Fact]
    public async Task Only_one_mode_write_is_in_flight_and_the_selector_is_busy_meanwhile()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.ReadOnlyReason = null;
        var writable = new[] { ListeningMode.Off, ListeningMode.Transparency, ListeningMode.NoiseCancellation };
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), writable, ListeningMode.Off);
        var sent = new List<SetListeningModeCommand>();
        t.OnSend = c =>
        {
            if (c is SetListeningModeCommand m) sent.Add(m);
            return new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware);
        };
        Assert.True(vm.IsModeInputEnabled);

        await vm.RequestModeAsync(ListeningMode.Transparency);
        Assert.True(vm.IsModePending);
        Assert.False(vm.IsModeInputEnabled);
        await vm.RequestModeAsync(ListeningMode.NoiseCancellation); // ignored while pending
        await vm.RequestModeAsync(ListeningMode.Off);               // ignored while pending
        Assert.Single(sent);
        Assert.Equal(ListeningMode.Transparency, sent[0].Mode);

        // The readback confirms it: input is accepted again.
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), writable, ListeningMode.Transparency);
        Assert.False(vm.IsModePending);
        Assert.True(vm.IsModeInputEnabled);
        await vm.RequestModeAsync(ListeningMode.NoiseCancellation);
        Assert.Equal(2, sent.Count);

        // A timeout is uncertain: end this session before sending another hardware write.
        time.Advance(HubViewModel.RequestTimeout);
        Assert.False(vm.IsModePending);
        Assert.False(vm.IsModeInputEnabled);
        Assert.Contains("End the session", vm.ModeReadOnlyText);
    }

    [Fact]
    public async Task Starting_a_session_waits_longer_than_the_transport_and_a_late_success_clears_the_error()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.Publish(nearby: true, session: false, audio: true, time.GetUtcNow());
        var transportOpen = new ClaspEngineOptions("C:/x/clasp-engine.exe", null, ScriptedHardwareTransport.Id, EngineSessionMode.Observe).OpenSessionTimeout;
        Assert.Equal(TimeSpan.FromSeconds(25), transportOpen);
        Assert.True(vm.EffectiveConnectTimeout >= transportOpen);
        Assert.Equal(HubViewModel.SessionConnectTimeout, vm.EffectiveConnectTimeout);

        await vm.ConnectAsync();
        time.Advance(HubViewModel.ConnectTimeout);  // the demo's 15 s: not over yet
        Assert.True(vm.IsConnecting);
        Assert.Null(vm.LastErrorText);

        time.Advance(HubViewModel.SessionConnectTimeout - HubViewModel.ConnectTimeout);
        Assert.False(vm.IsConnecting);
        Assert.Equal("Couldn't start session. Your AirPods didn't respond in time. Try again.", vm.LastErrorText);

        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow()); // it came up after all
        Assert.True(vm.IsConnected);
        Assert.Null(vm.LastErrorText);
    }

    [Fact]
    public async Task A_refused_start_uses_session_wording_and_is_cleared_by_a_later_session()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.Publish(nearby: true, session: false, audio: false, time.GetUtcNow());
        t.OnSend = c => new CommandResult(c.CommandId, CommandOutcome.Failed, DataOrigin.Hardware,
            "The AirPods are not connected to Windows. Connect the AirPods to Windows first (Bluetooth settings), then start the session");

        await vm.ConnectAsync();
        Assert.Equal("Couldn't start session: The AirPods are not connected to Windows. Connect the AirPods to Windows first (Bluetooth settings), then start the session.",
            vm.LastErrorText);

        t.OnSend = c => new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware);
        await vm.ConnectAsync();
        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow());
        Assert.Null(vm.LastErrorText);
    }

    [Fact]
    public async Task Without_ConnectControl_neither_connect_nor_disconnect_is_offered()
    {
        var (t, vm, time) = Create();
        await using var _ = vm;
        t.Capabilities = DeviceCapabilities.Battery | DeviceCapabilities.ListeningModes;
        int sends = 0;
        t.OnSend = c => { sends++; return new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware); };

        t.Publish(nearby: true, session: false, audio: true, time.GetUtcNow());
        Assert.False(vm.SupportsConnectControl);
        Assert.False(vm.IsConnectActionEnabled);
        await vm.ConnectAsync();

        t.Publish(nearby: true, session: true, audio: true, time.GetUtcNow());
        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnectActionEnabled);
        await vm.DisconnectAsync();
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task The_demo_keeps_its_own_wording()
    {
        await using var vm = new HubViewModel(new DemoAirPodsTransport(TestSupport.Instant()), DemoAirPodsTransport.DemoDeviceId, Dispatch);
        Assert.False(vm.IsControlSessionOnly);
        Assert.Equal("Connect", vm.ConnectActionText);
        Assert.Equal("", vm.BluetoothLinkText);
        Assert.Equal("", vm.ModeReadOnlyText);
        Assert.False(vm.HasUnavailableReason);
        Assert.Equal("Lowers media when you speak", vm.ConversationAwarenessCaption);
    }
}

public class HardwareTransportSelectionTests
{
    private const string Device = "--device=dev-3f9a1c0e8b7d6a52";

    [Fact]
    public void A_complete_hardware_request_yields_engine_options()
    {
        var choice = TransportSelection.FromArgs(["--transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe", Device, "--session=control",
            "--engine-sha256=" + new string('a', 64)]);
        Assert.True(choice.IsAvailable);
        Assert.False(choice.IsDemo);
        Assert.Equal(TransportKind.Hardware, choice.Kind);
        var options = Assert.IsType<ClaspEngineOptions>(choice.Hardware);
        Assert.Equal(EngineSessionMode.Control, options.SessionMode);
        Assert.Equal("dev-3f9a1c0e8b7d6a52", options.DeviceId);
        Assert.Equal(new string('A', 64), options.EngineSha256);
        Assert.Null(options.PipeName); // the per-user default
    }

    [Fact]
    public void The_session_defaults_to_observe()
    {
        var choice = TransportSelection.FromArgs(["--transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe", Device]);
        Assert.Equal(EngineSessionMode.Observe, choice.Hardware!.SessionMode);
    }

    [Theory]
    [InlineData("--engine-path is required", "--transport=hardware", Device)]
    [InlineData("--engine-path must be", "--transport=hardware", "--engine-path=clasp-engine.exe", Device)]
    [InlineData("--engine-path must be", "--transport=hardware", "--engine-path=//server/share/clasp-engine.exe", Device)]
    [InlineData("--device is required", "--transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe")]
    [InlineData("--device must be", "--transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe", "--device=AirPods Pro")]
    [InlineData("--engine-sha256 must be", "--transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe", Device, "--engine-sha256=1234")]
    [InlineData("Unknown session mode", "--transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe", Device, "--session=write")]
    [InlineData("Unknown transport", "--transport=bluetooth")]
    public void Unusable_hardware_requests_are_refused_and_never_become_the_demo(string reason, params string[] args)
    {
        var choice = TransportSelection.FromArgs(args);
        Assert.False(choice.IsAvailable);
        Assert.False(choice.IsDemo);
        Assert.Null(choice.Hardware);
        Assert.Contains(reason, choice.Explanation);
    }

    [Theory]
    [InlineData("Unrecognised argument '--transport'", "--transport", "hardware", "--engine-path=C:/Clasp/clasp-engine.exe", Device)]
    [InlineData("Unrecognised argument '--device'", "--transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe", "--device", "dev-3f9a1c0e8b7d6a52")]
    [InlineData("without --transport=hardware", "--engine-path=C:/Clasp/clasp-engine.exe", Device)]
    [InlineData("without --transport=hardware", Device)]
    [InlineData("without --transport=hardware", "--session=control")]
    [InlineData("without --transport=hardware", "--transport=demo", "--engine-sha256=" + "0000000000000000000000000000000000000000000000000000000000000000")]
    public void Malformed_or_partial_hardware_launches_never_start_the_demo(string reason, params string[] args)
    {
        var choice = TransportSelection.FromArgs(args);
        Assert.False(choice.IsAvailable);
        Assert.False(choice.IsDemo);
        Assert.Contains(reason, choice.Explanation);
    }

    [Fact]
    public void Unrelated_arguments_do_not_start_the_demo_without_the_flag()
    {
        Assert.False(TransportSelection.FromArgs(["--show=flyout", "--demo=nearby"]).IsDemo);
        Assert.True(TransportSelection.FromArgs(["--transport=demo", "--show=flyout", "--demo=nearby"]).IsDemo);
    }

    [Fact]
    public void The_demo_is_only_chosen_explicitly()
    {
        Assert.False(TransportSelection.FromArgs([]).IsDemo);
        Assert.True(TransportSelection.FromArgs(["--transport=demo"]).IsDemo);
        Assert.False(TransportSelection.FromArgs(["--transport=ntpods"]).IsDemo);
    }
}
