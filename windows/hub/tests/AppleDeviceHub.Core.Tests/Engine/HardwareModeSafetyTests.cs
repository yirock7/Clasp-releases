using System.Text.Json.Nodes;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Engine;
using AppleDeviceHub.Core.Interop;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Engine;

/// <summary>Offline regressions: a UI timeout cannot create a second uncertain device write.</summary>
public class HardwareModeSafetyTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(CommandOutcome.TimedOut)]
    [InlineData(CommandOutcome.Failed)]
    [InlineData(CommandOutcome.TransportUnavailable)]
    public void A_late_ack_and_readback_cannot_release_before_the_request_waits_terminal_outcome(CommandOutcome terminal)
    {
        // The request timeout/cancellation has won, but its continuation has not yet run.
        // Meanwhile, the read loop sees a late ACK and a genuine matching observation.
        var completion = new ModeWriteCompletionGate();
        completion.OnEngineReply(EngineOutcome.Accepted);
        completion.ObserveMatchingReadback();
        Assert.False(completion.CanRelease); // the reservation must still exist for Complete

        completion.Complete(terminal); // cancellation is mapped to TimedOut by the transport
        Assert.True(completion.IsUncertain);
        Assert.False(completion.CanRelease);
        completion.OnEngineReply(EngineOutcome.Accepted);
        completion.ObserveMatchingReadback();
        Assert.False(completion.CanRelease);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_successful_write_requires_ack_matching_readback_and_acknowledged_terminal_completion(bool terminalBeforeReadback)
    {
        var completion = new ModeWriteCompletionGate();
        completion.OnEngineReply(EngineOutcome.Accepted);
        if (terminalBeforeReadback) completion.Complete(CommandOutcome.Acknowledged);
        Assert.False(completion.CanRelease);
        completion.ObserveMatchingReadback();
        Assert.Equal(terminalBeforeReadback, completion.CanRelease);
        if (!terminalBeforeReadback) completion.Complete(CommandOutcome.Acknowledged);
        Assert.True(completion.CanRelease);
        Assert.False(completion.IsUncertain);
    }

    [Theory]
    [InlineData(EngineOutcome.Rejected, CommandOutcome.Rejected)]
    [InlineData(EngineOutcome.Unsupported, CommandOutcome.Unsupported)]
    public void An_engine_refusal_also_waits_for_the_terminal_request_outcome(EngineOutcome reply, CommandOutcome terminal)
    {
        var completion = new ModeWriteCompletionGate();
        completion.OnEngineReply(reply);
        Assert.False(completion.CanRelease);
        completion.Complete(terminal);
        Assert.True(completion.CanRelease);

        var racedTimeout = new ModeWriteCompletionGate();
        racedTimeout.OnEngineReply(reply);
        racedTimeout.Complete(CommandOutcome.TimedOut);
        Assert.False(racedTimeout.CanRelease);
        Assert.True(racedTimeout.IsUncertain);
    }

    private static async Task ObserveMode(FakeEngine engine, NtpodsDaemonTransport transport, string mode, long sessionId = 1)
    {
        await engine.SendAsync(FakeEngine.Observation(sessionId, "2026-10-15T18:04:05.000Z", new JsonObject { ["listening_mode"] = mode }));
        await transport.StartAsync(); // pipe barrier, not hardware I/O
    }

    private static async Task Open(NtpodsDaemonTransport transport)
    {
        await transport.StartAsync();
        Assert.Equal(CommandOutcome.Acknowledged, (await transport.SendAsync(new ConnectCommand(Guid.NewGuid()))).Outcome);
        await TestSupport.WaitUntil(() => transport.Current?.Link.ProtocolSession.Value == true);
    }

    [Fact]
    public async Task A_session_needs_a_genuine_mode_reading_before_the_first_write()
    {
        await using var engine = new FakeEngine();
        engine.AcceptSessions();
        await using var transport = new NtpodsDaemonTransport(FakeEngine.Options(engine));
        await Open(transport);

        Assert.Empty(transport.Current!.Listening.Supported);
        var refused = await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.Contains("not been reported", refused.Detail);
        Assert.DoesNotContain(engine.Received, m => (string?)m["type"] == "set_listening_mode");

        await ObserveMode(engine, transport, "noise_cancellation");
        Assert.Contains(ListeningMode.Transparency, transport.Current!.Listening.Supported);
        Assert.Equal(CommandOutcome.Acknowledged,
            (await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency))).Outcome);
    }

    [Fact]
    public async Task A_second_write_is_refused_until_the_first_reply_and_real_matching_readback()
    {
        await using var engine = new FakeEngine();
        engine.AcceptSessions();
        await using var transport = new NtpodsDaemonTransport(FakeEngine.Options(engine));
        await Open(transport);
        await ObserveMode(engine, transport, "noise_cancellation");
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_listening_mode" ? Task.CompletedTask : accept(m);

        var first = transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "set_listening_mode"));
        var refused = await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive));
        Assert.Equal(CommandOutcome.Rejected, refused.Outcome);
        Assert.Single(engine.Received, m => (string?)m["type"] == "set_listening_mode");

        var requestId = (string)engine.LastOfType("set_listening_mode")["request_id"]!;
        await engine.SendAsync(FakeEngine.Result(requestId, "accepted"));
        Assert.Equal(CommandOutcome.Acknowledged, (await first.WaitAsync(Prompt)).Outcome);
        Assert.Equal(CommandOutcome.Rejected,
            (await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive))).Outcome);

        await ObserveMode(engine, transport, "transparency");
        engine.OnRequest = accept;
        Assert.Equal(CommandOutcome.Acknowledged,
            (await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive))).Outcome);
    }

    [Fact]
    public async Task An_uncertain_transport_timeout_stays_locked_despite_late_ack_and_readback_until_a_new_session()
    {
        await using var engine = new FakeEngine();
        engine.AcceptSessions();
        var options = FakeEngine.Options(engine) with { RequestTimeout = TimeSpan.FromMilliseconds(150) };
        await using var transport = new NtpodsDaemonTransport(options);
        await Open(transport);
        await ObserveMode(engine, transport, "noise_cancellation");
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_listening_mode" ? Task.CompletedTask : accept(m);
        var first = transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency));
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "set_listening_mode"));
        Assert.Equal(CommandOutcome.TimedOut, (await first.WaitAsync(Prompt)).Outcome);
        var requestId = (string)engine.LastOfType("set_listening_mode")["request_id"]!;
        await engine.SendAsync(FakeEngine.Result(requestId, "accepted"));
        await ObserveMode(engine, transport, "transparency");

        Assert.Empty(transport.Current!.Listening.Supported);
        Assert.Equal(CommandOutcome.Rejected,
            (await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive))).Outcome);
        Assert.Single(engine.Received, m => (string?)m["type"] == "set_listening_mode");

        await transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => transport.Current?.Link.ProtocolSession.Value == false);
        engine.AcceptSessions(sessionId: 2);
        await Open(transport);
        Assert.Empty(transport.Current!.Listening.Supported);
        await ObserveMode(engine, transport, "transparency", sessionId: 2);
        Assert.Equal(CommandOutcome.Acknowledged,
            (await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive))).Outcome);
    }

    [Fact]
    public async Task The_shorter_ui_timeout_never_releases_a_still_outstanding_wire_write()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var engine = new FakeEngine();
        engine.AcceptSessions();
        var options = FakeEngine.Options(engine) with { RequestTimeout = TimeSpan.FromSeconds(10) };
        await using var transport = new NtpodsDaemonTransport(options, time);
        var uiGate = new object();
        await using var vm = new HubViewModel(transport, EngineFixtures.DeviceId, action => { lock (uiGate) action(); }, time: time);
        await Open(transport);
        await ObserveMode(engine, transport, "noise_cancellation");
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_listening_mode" ? Task.CompletedTask : accept(m);
        var first = vm.RequestModeAsync(ListeningMode.Transparency);
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "set_listening_mode"));

        time.Advance(HubViewModel.RequestTimeout);
        Assert.False(vm.IsModePending);
        Assert.False(vm.IsModeInputEnabled);
        Assert.Contains("End the session", vm.ModeReadOnlyText);
        await vm.RequestModeAsync(ListeningMode.Adaptive).WaitAsync(Prompt);
        Assert.Single(engine.Received, m => (string?)m["type"] == "set_listening_mode");

        var requestId = (string)engine.LastOfType("set_listening_mode")["request_id"]!;
        await engine.SendAsync(FakeEngine.Result(requestId, "accepted"));
        await first.WaitAsync(Prompt);
        await ObserveMode(engine, transport, "transparency");
        Assert.Equal(ListeningMode.Transparency, vm.ActualMode); // a late real reading still displays honestly
        Assert.False(vm.IsModeInputEnabled); // uncertainty retires only at a session boundary

        await transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => transport.Current?.Link.ProtocolSession.Value == false);
        engine.AcceptSessions(sessionId: 2);
        await Open(transport);
        await ObserveMode(engine, transport, "transparency", sessionId: 2);
        Assert.True(vm.IsModeInputEnabled);
    }

    [Fact]
    public async Task Cancelling_a_sent_write_cannot_clear_its_uncertain_session_reservation()
    {
        await using var engine = new FakeEngine();
        engine.AcceptSessions();
        await using var transport = new NtpodsDaemonTransport(FakeEngine.Options(engine));
        await Open(transport);
        await ObserveMode(engine, transport, "noise_cancellation");
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "set_listening_mode" ? Task.CompletedTask : accept(m);
        using var cancel = new CancellationTokenSource();
        var first = transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency), cancel.Token);
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "set_listening_mode"));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(NtpodsDaemonTransport.UncertainModeWriteReason, transport.ReadOnlyReason);
        Assert.Equal(CommandOutcome.Rejected,
            (await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Adaptive))).Outcome);
        Assert.Single(engine.Received, m => (string?)m["type"] == "set_listening_mode");
    }

    [Fact]
    public async Task Ending_a_session_disables_writes_before_the_close_answer_arrives()
    {
        await using var engine = new FakeEngine();
        engine.AcceptSessions();
        await using var transport = new NtpodsDaemonTransport(FakeEngine.Options(engine));
        await Open(transport);
        await ObserveMode(engine, transport, "noise_cancellation");
        var accept = engine.OnRequest!;
        engine.OnRequest = m => (string?)m["type"] == "close_session" ? Task.CompletedTask : accept(m);
        var close = transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await TestSupport.WaitUntil(() => engine.Received.Any(m => (string?)m["type"] == "close_session"));
        Assert.Empty(transport.Current!.Listening.Supported);
        Assert.Equal(CommandOutcome.Rejected,
            (await transport.SendAsync(new SetListeningModeCommand(Guid.NewGuid(), ListeningMode.Transparency))).Outcome);
        Assert.DoesNotContain(engine.Received, m => (string?)m["type"] == "set_listening_mode");
        await engine.SendAsync(FakeEngine.Result((string)engine.LastOfType("close_session")["request_id"]!, "accepted"));
        await engine.SendAsync(FakeEngine.Session(1, "closed"));
        Assert.Equal(CommandOutcome.Acknowledged, (await close.WaitAsync(Prompt)).Outcome);
    }

    [Fact]
    public async Task The_ui_needs_a_reported_mode_and_a_failed_end_cannot_clear_uncertainty()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedHardwareTransport { ReadOnlyReason = null };
        await using var vm = new HubViewModel(transport, EngineFixtures.DeviceId, action => action(), time: time);
        var writable = new[] { ListeningMode.NoiseCancellation, ListeningMode.Transparency, ListeningMode.Adaptive };
        var sent = new List<DeviceCommand>();
        transport.OnSend = command =>
        {
            sent.Add(command);
            return new CommandResult(command.CommandId,
                command is DisconnectCommand ? CommandOutcome.Failed : CommandOutcome.Acknowledged, DataOrigin.Hardware);
        };
        transport.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), writable);
        Assert.False(vm.IsModeInputEnabled);
        Assert.Contains("not been reported", vm.ModeReadOnlyText);
        await vm.RequestModeAsync(ListeningMode.Transparency);
        Assert.Empty(sent);

        transport.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), writable, ListeningMode.NoiseCancellation);
        Assert.True(vm.IsModeInputEnabled);
        await vm.RequestModeAsync(ListeningMode.Transparency);
        time.Advance(HubViewModel.RequestTimeout);
        transport.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), writable, ListeningMode.Transparency);
        Assert.False(vm.IsModeInputEnabled);
        await vm.DisconnectAsync(); // a rejected/failed End is not a session boundary
        Assert.False(vm.IsDisconnectPending);
        Assert.True(vm.IsConnected);
        Assert.False(vm.IsModeInputEnabled);
        await vm.RequestModeAsync(ListeningMode.Adaptive);
        Assert.Single(sent.OfType<SetListeningModeCommand>());

        transport.Publish(nearby: true, session: false, audio: true, time.GetUtcNow());
        transport.Publish(nearby: true, session: true, audio: true, time.GetUtcNow(), writable, ListeningMode.NoiseCancellation);
        Assert.True(vm.IsModeInputEnabled);
    }
}
