using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests;

/// <summary>
/// Hardware-readiness packet, review findings HR-04 (connect attempt identity and stalled
/// "Connecting") and HR-05 (Disconnect and session loss retire pending setting work).
/// Every command can be held so completions arrive in a chosen order; time is fake.
/// </summary>
public class ConnectionLifecycleTests
{
    private static readonly object Gate = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    /// <summary>Records every command and holds its result until the test releases it.</summary>
    private sealed class HeldSends
    {
        public readonly List<DeviceCommand> Sent = new();
        private readonly Dictionary<Guid, TaskCompletionSource<CommandResult>> _pending = new();

        public HeldSends(ScriptedTransport transport)
        {
            transport.OnSendAsync = c =>
            {
                var tcs = new TaskCompletionSource<CommandResult>();
                lock (_pending) { Sent.Add(c); _pending[c.CommandId] = tcs; }
                return tcs.Task;
            };
        }

        public T Last<T>() where T : DeviceCommand => Sent.OfType<T>().Last();

        public int Count<T>() where T : DeviceCommand => Sent.OfType<T>().Count();

        public void Complete(DeviceCommand command, CommandOutcome outcome, string? detail = null)
            => _pending[command.CommandId].SetResult(new CommandResult(command.CommandId, outcome, DataOrigin.Hardware, detail));
    }

    private static Observation<bool> On(DateTimeOffset at) => Observation<bool>.At(true, at);

    private static Observation<bool> Off(DateTimeOffset at) => Observation<bool>.At(false, at);

    private static LinkState Nearby(DateTimeOffset at) => new(On(at), Off(at), Off(at), Off(at));

    private static LinkState EngineConnecting(DateTimeOffset at) => new(On(at), On(at), Off(at), Off(at));

    private static LinkState Connected(DateTimeOffset at) => new(On(at), Off(at), On(at), On(at));

    // ---- HR-04 ---------------------------------------------------------------------------

    [Fact]
    public async Task A_late_failure_from_a_timed_out_attempt_does_not_end_the_next_attempt()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var held = new HeldSends(transport);
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(null, time.GetUtcNow(), link: Nearby(time.GetUtcNow()));

        var attemptA = vm.ConnectAsync();
        var a = held.Last<ConnectCommand>();
        time.Advance(HubViewModel.ConnectTimeout);                 // A times out
        Assert.False(vm.IsConnecting);

        var attemptB = vm.ConnectAsync();                          // the user tries again
        var b = held.Last<ConnectCommand>();
        Assert.NotEqual(a.CommandId, b.CommandId);
        Assert.True(vm.IsConnecting);

        held.Complete(a, CommandOutcome.Failed, "radio busy");     // A's answer arrives late
        await attemptA;

        Assert.True(vm.IsConnecting);                              // B is unaffected
        Assert.DoesNotContain("radio busy", vm.LastErrorText ?? "");

        held.Complete(b, CommandOutcome.Acknowledged);
        await attemptB;
    }

    [Fact]
    public async Task An_engine_stuck_in_connecting_ends_after_the_timeout_and_allows_a_retry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(null, time.GetUtcNow(), link: EngineConnecting(time.GetUtcNow()));
        Assert.True(vm.IsConnecting);

        time.Advance(HubViewModel.ConnectTimeout);
        // The engine keeps reporting Connecting (for example a repeated snapshot).
        transport.Publish(null, time.GetUtcNow(), link: EngineConnecting(time.GetUtcNow()));

        Assert.False(vm.IsConnecting);
        Assert.True(vm.IsConnectActionEnabled);                    // retry is possible
        Assert.Contains("Keep the case open", vm.LastErrorText);

        // A real later connection is still shown truthfully.
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow(), link: Connected(time.GetUtcNow()));
        Assert.True(vm.IsConnected);
    }

    // ---- HR-05 ---------------------------------------------------------------------------

    [Fact]
    public async Task Disconnect_drops_queued_strength_even_if_the_earlier_ack_arrives_first()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var held = new HeldSends(transport);
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.Adaptive, time.GetUtcNow(), strength: 40, link: Connected(time.GetUtcNow()));

        var strength = vm.SetAdaptiveStrengthAsync(60);            // 60 in flight
        await vm.SetAdaptiveStrengthAsync(70);                     // 70 queued
        var disconnect = vm.DisconnectAsync();
        held.Complete(held.Last<DisconnectCommand>(), CommandOutcome.Acknowledged);
        await disconnect;

        held.Complete(held.Last<SetAdaptiveStrengthCommand>(), CommandOutcome.Acknowledged); // late ack for 60
        await strength;

        Assert.Equal(1, held.Count<SetAdaptiveStrengthCommand>()); // 70 never sent
    }

    [Fact]
    public async Task A_second_disconnect_click_while_the_first_is_outstanding_sends_nothing()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var held = new HeldSends(transport);
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow(), link: Connected(time.GetUtcNow()));

        var first = vm.DisconnectAsync();
        var second = vm.DisconnectAsync();

        Assert.Equal(1, held.Count<DisconnectCommand>());
        held.Complete(held.Last<DisconnectCommand>(), CommandOutcome.Acknowledged);
        await first;
        await second;
    }

    [Fact]
    public async Task Losing_the_session_retires_a_pending_mode_change_without_a_later_timeout_error()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        var held = new HeldSends(transport);
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow(), link: Connected(time.GetUtcNow()));

        var request = vm.RequestModeAsync(ListeningMode.Transparency);
        Assert.Equal(ListeningMode.Transparency, vm.DisplayedMode);

        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow(), link: Nearby(time.GetUtcNow())); // session lost
        time.Advance(HubViewModel.RequestTimeout * 2);

        Assert.Null(vm.LastErrorText);                             // no "No confirmation" for a retired request
        Assert.NotEqual(ListeningMode.Transparency, vm.DisplayedMode);

        held.Complete(held.Last<SetListeningModeCommand>(), CommandOutcome.Acknowledged); // late, old-session ack
        await request;
        Assert.Null(vm.LastErrorText);
    }

    [Fact]
    public async Task A_disconnect_that_never_lands_times_out_with_a_message()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var transport = new ScriptedTransport();
        await using var vm = new HubViewModel(transport, transport.DeviceId, Dispatch, time: time);
        transport.Publish(ListeningMode.NoiseCancellation, time.GetUtcNow(), link: Connected(time.GetUtcNow()));

        await vm.DisconnectAsync();                                // acknowledged; no state change follows
        Assert.True(vm.IsDisconnectPending);
        Assert.False(vm.IsConnectActionEnabled);

        time.Advance(HubViewModel.DisconnectTimeout);

        Assert.False(vm.IsDisconnectPending);
        Assert.Contains("Couldn't disconnect", vm.LastErrorText);
        Assert.True(vm.IsConnectActionEnabled);
    }
}
