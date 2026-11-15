using AppleDeviceHub.Core.Automation;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Connection;

/// <summary>
/// Security finding 1: the origin of a connect belongs to one attempt. A failed explicit connect must
/// never leave behind a "the user asked" mark that a later nearby auto-connect inherits.
/// </summary>
public sealed class ConnectionOriginTests
{
    private const string ContainerA = "0a91ec1f-5b8e-49cf-b411-e3e359b34a77";
    private const string ContainerB = "f6a76cac-39d4-49c8-99cb-011e272ffab0";
    private const string Endpoint = "{0.0.0.00000000}.{airpods}";
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionActionSettings AllOn = new(true, true, 25, true);

    // ----- the tracker on its own -----

    [Fact]
    public void An_attempt_that_does_not_connect_leaves_no_origin()
    {
        foreach (var origin in new[] { ConnectionOrigin.User, ConnectionOrigin.Hotkey, ConnectionOrigin.ExternalApproved, ConnectionOrigin.NearbyAuto })
        {
            var tracker = new ConnectionAttemptTracker();
            var id = tracker.Begin(origin, "a", T0);
            Assert.Equal(origin, tracker.Current("a", T0));
            tracker.Complete(id, connected: false, T0);
            Assert.Equal(ConnectionOrigin.None, tracker.Current("a", T0));
        }
    }

    [Fact]
    public void A_nearby_attempt_never_inherits_a_leftover_explicit_origin()
    {
        var tracker = new ConnectionAttemptTracker();
        tracker.Begin(ConnectionOrigin.User, "a", T0); // never completed: a leftover
        // The explicit attempt is "still running" here, so it keeps owning the connection...
        Assert.Equal(ConnectionAttemptTracker.NoAttempt, tracker.Begin(ConnectionOrigin.NearbyAuto, "a", T0));
        Assert.Equal(ConnectionOrigin.User, tracker.Current("a", T0));

        // ...but once it is over (or too old to be running), nearby is always NearbyAuto.
        var afterLimit = T0 + ConnectionAttemptTracker.RunningAttemptLimit;
        var auto = tracker.Begin(ConnectionOrigin.NearbyAuto, "a", afterLimit);
        Assert.NotEqual(ConnectionAttemptTracker.NoAttempt, auto);
        Assert.Equal(ConnectionOrigin.NearbyAuto, tracker.Current("a", afterLimit));
    }

    [Fact]
    public void A_nearby_attempt_discards_the_origin_a_finished_explicit_attempt_kept()
    {
        var tracker = new ConnectionAttemptTracker();
        var user = tracker.Begin(ConnectionOrigin.User, "a", T0);
        tracker.Complete(user, connected: true, T0);
        Assert.Equal(ConnectionOrigin.User, tracker.Current("a", T0));

        tracker.Begin(ConnectionOrigin.NearbyAuto, "a", T0.AddSeconds(5));
        Assert.Equal(ConnectionOrigin.NearbyAuto, tracker.Current("a", T0.AddSeconds(5)));
    }

    [Fact]
    public void An_unknown_origin_is_the_least_trusted_one()
    {
        var tracker = new ConnectionAttemptTracker();
        tracker.Begin(ConnectionOrigin.None, "a", T0);
        Assert.Equal(ConnectionOrigin.NearbyAuto, tracker.Current("a", T0));
        tracker.Begin((ConnectionOrigin)99, "a", T0);
        Assert.Equal(ConnectionOrigin.NearbyAuto, tracker.Current("a", T0));
    }

    [Fact]
    public void A_stale_attempt_id_changes_nothing_and_the_origin_is_bound_to_its_device()
    {
        var tracker = new ConnectionAttemptTracker();
        var first = tracker.Begin(ConnectionOrigin.User, "a", T0);
        var second = tracker.Begin(ConnectionOrigin.Hotkey, "a", T0);
        tracker.Complete(first, connected: false, T0); // superseded: must not clear the newer attempt
        Assert.Equal(ConnectionOrigin.Hotkey, tracker.Current("a", T0));
        Assert.Equal(ConnectionOrigin.None, tracker.Current("b", T0)); // another device
        Assert.Equal(ConnectionOrigin.None, tracker.Current("a", T0)); // and it was dropped, not kept

        tracker.Complete(second, connected: true, T0);
        Assert.Equal(ConnectionOrigin.None, tracker.Current("a", T0));
    }

    [Fact]
    public void A_produced_connection_origin_expires_and_is_consumed_once()
    {
        var tracker = new ConnectionAttemptTracker();
        tracker.Complete(tracker.Begin(ConnectionOrigin.User, "a", T0), connected: true, T0);
        Assert.Equal(ConnectionOrigin.User, tracker.Current("a", T0.AddSeconds(29)));
        Assert.Equal(ConnectionOrigin.None, tracker.Current("a", T0 + ConnectionAttemptTracker.ProducedConnectionWindow));

        tracker.Complete(tracker.Begin(ConnectionOrigin.User, "a", T0), connected: true, T0);
        tracker.Consume();
        Assert.Equal(ConnectionOrigin.None, tracker.Current("a", T0));
    }

    // ----- the view model: every terminal outcome clears the origin -----

    [Theory]
    [InlineData(ConnectionAttemptKind.Failed)]
    [InlineData(ConnectionAttemptKind.Unsupported)]
    public async Task A_failed_or_unsupported_explicit_connect_clears_its_origin(ConnectionAttemptKind kind)
    {
        var h = await Harness.CreateAsync((_, _, _) => Task.FromResult(new ConnectionAttemptResult(kind, "refused")));

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect);

        Assert.Equal(ConnectionOrigin.None, h.ViewModel.CurrentConnectionOrigin);
    }

    [Fact]
    public async Task A_busy_give_up_clears_the_origin()
    {
        var h = await Harness.CreateAsync((_, _, _) =>
            Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Busy, "busy")));

        var connect = h.ViewModel.ConnectAsync(ConnectionOrigin.Hotkey);
        Assert.Equal(ConnectionOrigin.Hotkey, h.ViewModel.CurrentConnectionOrigin);
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(100), maxSteps: 80);

        Assert.Equal(ConnectionOrigin.None, h.ViewModel.CurrentConnectionOrigin);
    }

    [Fact]
    public async Task An_exception_clears_the_origin()
    {
        var h = await Harness.CreateAsync((_, _, _) => Task.FromException<ConnectionAttemptResult>(new InvalidOperationException("boom")));

        var connect = h.ViewModel.ConnectAsync(ConnectionOrigin.ExternalApproved);
        await h.RunToCompletionAsync(connect);

        Assert.Equal(ConnectionOrigin.None, h.ViewModel.CurrentConnectionOrigin);
    }

    [Fact]
    public async Task Cancelling_the_attempt_by_changing_the_selection_clears_the_origin()
    {
        var h = await Harness.CreateAsync(Attempted, twoDevices: true);
        h.Controller.IsBusy = true; // the click waits, then is cancelled

        var connect = h.ViewModel.ConnectAsync();
        for (var i = 0; i < 3; i++) await h.StepAsync(TimeSpan.FromMilliseconds(100), connect);
        Assert.Equal(ConnectionOrigin.User, h.ViewModel.CurrentConnectionOrigin);

        h.ViewModel.SelectedDeviceId = "device-b";
        await TestSupport.WaitUntil(() => connect.IsCompleted);

        Assert.Equal(ConnectionOrigin.None, h.ViewModel.CurrentConnectionOrigin);
    }

    [Fact]
    public async Task An_attempt_that_times_out_without_a_connection_clears_its_origin()
    {
        var h = await Harness.CreateAsync(Attempted);

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400), maxSteps: 100);

        Assert.False(h.ViewModel.IsConnected);
        Assert.Contains(h.Logs, line => line.StartsWith("Connect: timed out", StringComparison.Ordinal));
        Assert.Equal(ConnectionOrigin.None, h.ViewModel.CurrentConnectionOrigin);
    }

    // ----- the finding's sequence, end to end -----

    [Fact]
    public async Task A_failed_explicit_connect_then_a_nearby_auto_connect_stays_NearbyAuto_and_never_plans_the_user_actions()
    {
        var calls = 0;
        var h = await Harness.CreateAsync((_, _, _) => Task.FromResult(++calls == 1
            ? new ConnectionAttemptResult(ConnectionAttemptKind.Failed, "refused")
            : new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted")), autoConnect: true);

        await h.RunToCompletionAsync(h.ViewModel.ConnectAsync()); // the explicit connect fails
        Assert.Equal(ConnectionOrigin.None, h.ViewModel.CurrentConnectionOrigin);

        h.ConnectedAfter = h.Elapsed + TimeSpan.FromSeconds(1);
        h.ViewModel.NotifyNearbyEpisode(true); // a (possibly spoofed) nearby signal
        Assert.Equal(2, h.Controller.ReconnectCalls);
        Assert.True(h.ViewModel.IsConnecting);
        Assert.Equal(ConnectionOrigin.NearbyAuto, h.ViewModel.CurrentConnectionOrigin);

        for (var i = 0; i < 30 && h.ViewModel.IsConnecting; i++)
            await h.StepAsync(TimeSpan.FromMilliseconds(400), () => !h.ViewModel.IsConnecting);
        Assert.True(h.ViewModel.IsConnected);
        // The connection was produced by the nearby attempt, so that is the origin the policy gets.
        var origin = h.ViewModel.CurrentConnectionOrigin;
        Assert.Equal(ConnectionOrigin.NearbyAuto, origin);

        var policy = new ConnectionActionsPolicy();
        Assert.Empty(policy.Evaluate(new ConnectionObservation(false, null, false, false), AllOn, new(true, false), T0).Actions);
        var plan = policy.Evaluate(
            new ConnectionObservation(true, Endpoint, true, false),
            AllOn,
            new ConnectionActionContext(false, false, Origin: origin),
            T0.AddSeconds(5));

        // Only the user's own "switch output" setting applies. Never the unconditional switch for a
        // person's request, never volume, never play.
        Assert.DoesNotContain(ConnectionActionKind.SetVolume, plan.Actions.Select(a => a.Kind));
        Assert.DoesNotContain(ConnectionActionKind.Play, plan.Actions.Select(a => a.Kind));
        Assert.False(new ConnectionActionContext(false, false, Origin: origin).UserRequestedConnect);
        var settingsOff = new ConnectionActionSettings(true, true, 25, false);
        var policy2 = new ConnectionActionsPolicy();
        policy2.Evaluate(new ConnectionObservation(false, null, false, false), settingsOff, new(true, false), T0);
        Assert.Empty(policy2.Evaluate(
            new ConnectionObservation(true, Endpoint, true, false),
            settingsOff,
            new ConnectionActionContext(false, false, Origin: origin),
            T0.AddSeconds(5)).Actions);
    }

    [Fact]
    public async Task A_successful_explicit_connect_still_applies_the_users_configured_actions()
    {
        var h = await Harness.CreateAsync(Attempted);
        h.ConnectedAfter = TimeSpan.FromSeconds(1);

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400), maxSteps: 100);
        Assert.True(h.ViewModel.IsConnected);

        var origin = h.ViewModel.CurrentConnectionOrigin;
        Assert.Equal(ConnectionOrigin.User, origin); // kept for the policy that plans this connection

        var policy = new ConnectionActionsPolicy();
        policy.Evaluate(new ConnectionObservation(false, null, false, false), AllOn, new(true, false), T0);
        var plan = policy.Evaluate(
            new ConnectionObservation(true, Endpoint, true, false),
            AllOn,
            new ConnectionActionContext(false, false, Origin: origin),
            T0.AddSeconds(5));

        Assert.Equal(
            [ConnectionActionKind.SwitchOutput, ConnectionActionKind.SetVolume, ConnectionActionKind.Play],
            plan.Actions.Select(a => a.Kind).ToArray());

        h.ViewModel.ConsumeConnectionOrigin(); // the host has used it
        Assert.Equal(ConnectionOrigin.None, h.ViewModel.CurrentConnectionOrigin);
    }

    private static Task<ConnectionAttemptResult> Attempted(string device, string container, CancellationToken token)
        => Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted"));

    private sealed class Harness
    {
        public required CountingClock Clock { get; init; }
        public required ScriptedController Controller { get; init; }
        public required CompanionConnectionViewModel ViewModel { get; init; }
        public required List<string> Logs { get; init; }
        public required DateTimeOffset Start { get; init; }

        public TimeSpan? ConnectedAfter { get; set; }

        public TimeSpan Elapsed => Clock.GetUtcNow() - Start;

        public static async Task<Harness> CreateAsync(
            Func<string, string, CancellationToken, Task<ConnectionAttemptResult>> reconnect,
            bool twoDevices = false,
            bool autoConnect = false)
        {
            var clock = new CountingClock(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
            var logs = new List<string>();
            var controller = new ScriptedController { Reconnect = reconnect };
            var settings = new HubSettings
            {
                AirPodsDeviceId = "device-a",
                ObserveNearbyAirPods = autoConnect,
                AutoConnectAirPods = autoConnect,
            };
            var viewModel = new CompanionConnectionViewModel(
                controller,
                settings,
                action => action(),
                clock,
                autoConnectIdentity: () => IdentityConfidence.LikelyYours,
                log: line => { lock (logs) logs.Add(line); });
            var harness = new Harness
            {
                Clock = clock,
                Controller = controller,
                ViewModel = viewModel,
                Logs = logs,
                Start = clock.GetUtcNow(),
            };
            controller.Read = _ => Task.FromResult(harness.BuildSnapshot(twoDevices));
            await viewModel.RefreshAsync();
            return harness;
        }

        private ConnectionSnapshot BuildSnapshot(bool twoDevices)
        {
            var connected = ConnectedAfter is { } after && Elapsed >= after;
            var devices = new List<ConnectionDevice> { new("device-a", "AirPods A", ContainerA, true, false, false, true, null) };
            var endpoints = new List<ConnectionAudioEndpoint>
            {
                new("endpoint-a", "AirPods A", ContainerA, connected, connected, true),
            };
            if (twoDevices)
            {
                devices.Add(new("device-b", "AirPods B", ContainerB, true, false, false, true, null));
                endpoints.Add(new("endpoint-b", "AirPods B", ContainerB, false, false, true));
            }
            return new ConnectionSnapshot(true, devices, endpoints, null);
        }

        public async Task StepAsync(TimeSpan span, Task connect)
            => await StepAsync(span, () => connect.IsCompleted);

        public async Task StepAsync(TimeSpan span, Func<bool> done)
        {
            var before = Volatile.Read(ref Clock.Timers);
            Clock.Advance(span);
            await TestSupport.WaitUntil(() => done() || Volatile.Read(ref Clock.Timers) > before);
        }

        public async Task RunToCompletionAsync(Task connect, TimeSpan? step = null, int maxSteps = 100)
        {
            var interval = step ?? TimeSpan.FromMilliseconds(400);
            for (var i = 0; i < maxSteps && !connect.IsCompleted; i++) await StepAsync(interval, connect);
            await connect;
        }
    }

    private sealed class CountingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public int Timers;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref Timers);
            return timer;
        }
    }

    private sealed class ScriptedController : IAirPodsConnectionController
    {
        private int _reconnectCalls;

        public Func<CancellationToken, Task<ConnectionSnapshot>>? Read { get; set; }
        public Func<string, string, CancellationToken, Task<ConnectionAttemptResult>>? Reconnect { get; set; }
        public bool IsBusy { get; set; }
        public int ReconnectCalls => Volatile.Read(ref _reconnectCalls);

        public event EventHandler? Changed { add { } remove { } }

        public Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
            => Read!(cancellationToken);

        public Task<ConnectionAttemptResult> ReconnectAsync(
            string deviceId, string expectedContainerId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reconnectCalls);
            return Reconnect!(deviceId, expectedContainerId, cancellationToken);
        }
    }
}
