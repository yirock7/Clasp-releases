using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Connection;

/// <summary>
/// "Connect works on the first click": a click during a running metadata read waits instead of
/// vanishing, a Busy answer is retried, and a user connect waits ~30 s and sends one automatic
/// second request at ~8 s. Time is fully fake; each step waits until the connect code has parked
/// on its next delay, so the tests do not depend on wall-clock speed.
/// </summary>
public sealed class ConnectFirstClickTests
{
    private const string ContainerA = "0a91ec1f-5b8e-49cf-b411-e3e359b34a77";
    private const string ContainerB = "f6a76cac-39d4-49c8-99cb-011e272ffab0";
    private const string BusyText = "Windows is busy. Try again in a moment.";
    private const string UserTimeoutText = "Not connected yet. Make sure they're out of the case and nearby, then try again.";

    private static readonly Func<string, string, CancellationToken, Task<ConnectionAttemptResult>> AlwaysAttempted =
        (_, _, _) => Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted"));

    [Fact]
    public async Task A_click_during_a_running_read_waits_for_it_then_sends_exactly_one_request()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);
        h.Controller.IsBusy = true; // the flyout's metadata read is running

        var connect = h.ViewModel.ConnectAsync();

        Assert.True(h.ViewModel.IsConnecting);
        Assert.Equal(0, h.Controller.ReconnectCalls);
        Assert.Contains(h.Logs, line => line.Contains("click accepted", StringComparison.Ordinal));
        for (var i = 0; i < 5; i++) await h.StepAsync(TimeSpan.FromMilliseconds(100), connect);
        Assert.Equal(0, h.Controller.ReconnectCalls);
        Assert.True(h.ViewModel.IsConnecting);

        h.Controller.IsBusy = false; // the read finished
        await h.StepAsync(TimeSpan.FromMilliseconds(100), () => h.Controller.ReconnectCalls == 1);
        h.ConnectedAfter = TimeSpan.Zero; // the next poll sees the AirPods connected
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400));

        Assert.Equal(1, h.Controller.ReconnectCalls);
        Assert.True(h.ViewModel.IsConnected);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.Contains(h.Logs, line => line.Contains("waiting for Windows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_busy_answer_from_the_controller_is_retried()
    {
        var results = new Queue<ConnectionAttemptKind>([
            ConnectionAttemptKind.Busy, ConnectionAttemptKind.Busy, ConnectionAttemptKind.Attempted]);
        var h = await Harness.CreateAsync((_, _, _) =>
            Task.FromResult(new ConnectionAttemptResult(results.Dequeue(), "scripted")));
        h.ConnectedAfter = TimeSpan.Zero;

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(100));

        Assert.Equal(3, h.Controller.ReconnectCalls);
        Assert.True(h.ViewModel.IsConnected);
        Assert.Equal(1, h.Controller.MaxConcurrentRequests);
    }

    [Fact]
    public async Task Still_blocked_by_a_running_read_ends_with_a_visible_message_and_never_sends()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);
        h.Controller.IsBusy = true;

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(100), maxSteps: 80);

        Assert.Equal(0, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.Equal(BusyText, h.ViewModel.DetailText);
        Assert.Contains(h.Logs, line => line.Contains("gave up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Busy_answers_for_the_whole_bound_end_with_the_busy_message()
    {
        var h = await Harness.CreateAsync((_, _, _) =>
            Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Busy, "controller text")));

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(100), maxSteps: 80);

        Assert.True(h.Controller.ReconnectCalls > 1);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.Equal(BusyText, h.ViewModel.DetailText);
        Assert.Equal(1, h.Controller.MaxConcurrentRequests);
    }

    [Fact]
    public async Task No_connection_at_8_seconds_sends_exactly_one_retry_and_a_connection_at_12_seconds_succeeds()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);
        h.ConnectedAfter = TimeSpan.FromSeconds(12);

        var connect = h.ViewModel.ConnectAsync();
        var callsAtFiveSeconds = -1;
        var callsAtTenSeconds = -1;
        for (var i = 0; i < 100 && !connect.IsCompleted; i++)
        {
            await h.StepAsync(TimeSpan.FromMilliseconds(400), connect);
            Assert.True(connect.IsCompleted || h.ViewModel.IsConnecting, "the UI must say Connecting throughout");
            if (callsAtFiveSeconds < 0 && h.Elapsed >= TimeSpan.FromSeconds(5)) callsAtFiveSeconds = h.Controller.ReconnectCalls;
            if (callsAtTenSeconds < 0 && h.Elapsed >= TimeSpan.FromSeconds(10)) callsAtTenSeconds = h.Controller.ReconnectCalls;
        }
        await connect;

        Assert.Equal(1, callsAtFiveSeconds);
        Assert.Equal(2, callsAtTenSeconds);
        Assert.Equal(2, h.Controller.ReconnectCalls);
        Assert.Equal(1, h.Controller.MaxConcurrentRequests);
        Assert.True(h.ViewModel.IsConnected);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.Equal("Connected and set as your sound output", h.ViewModel.StatusText);
        Assert.DoesNotContain("Not connected yet", h.ViewModel.DetailText, StringComparison.Ordinal);
        Assert.Contains(h.Logs, line => line.StartsWith("Connect: retry sent", StringComparison.Ordinal));
        Assert.Contains(h.Logs, line => line.StartsWith("Connect: connected after 12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_connection_at_8_seconds_sends_one_retry_and_a_connection_at_25_seconds_still_succeeds()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);
        h.ConnectedAfter = TimeSpan.FromSeconds(25);

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400), maxSteps: 100);

        Assert.Equal(2, h.Controller.ReconnectCalls);
        Assert.Equal(1, h.Controller.MaxConcurrentRequests);
        Assert.True(h.ViewModel.IsConnected);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.DoesNotContain("Not connected yet", h.ViewModel.DetailText, StringComparison.Ordinal);
        Assert.Contains(h.Logs, line => line.StartsWith("Connect: retry sent", StringComparison.Ordinal));
        Assert.Contains(h.Logs, line => line.StartsWith("Connect: connected after 25", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Never_connected_times_out_honestly_at_the_user_window_after_one_retry()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400), maxSteps: 100);

        Assert.Equal(2, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.False(h.ViewModel.IsConnected);
        Assert.Equal(UserTimeoutText, h.ViewModel.DetailText);
        Assert.InRange(h.Elapsed, CompanionConnectionViewModel.UserConnectTotalWindow,
            CompanionConnectionViewModel.UserConnectTotalWindow + TimeSpan.FromSeconds(1));
        Assert.Contains(h.Logs, line => line.StartsWith(
            $"Connect: timed out after {(int)CompanionConnectionViewModel.UserConnectTotalWindow.TotalSeconds}",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ConnectionAttemptKind.Failed)]
    [InlineData(ConnectionAttemptKind.Unsupported)]
    public async Task A_terminal_failure_on_the_retry_ends_the_wait_with_its_reason(ConnectionAttemptKind kind)
    {
        var calls = 0;
        var h = await Harness.CreateAsync((_, _, _) => Task.FromResult(++calls == 1
            ? new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted")
            : new ConnectionAttemptResult(kind, "Windows refused the second request.")));

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400), maxSteps: 100);

        Assert.Equal(2, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.Equal("Windows refused the second request.", h.ViewModel.DetailText);
        Assert.True(h.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_timed_out_retry_keeps_waiting_without_a_third_request()
    {
        var calls = 0;
        var h = await Harness.CreateAsync((_, _, _) => Task.FromResult(++calls == 1
            ? new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted")
            : new ConnectionAttemptResult(ConnectionAttemptKind.TimedOut, "still running")));
        h.ConnectedAfter = TimeSpan.FromSeconds(15);

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400), maxSteps: 100);

        Assert.Equal(2, h.Controller.ReconnectCalls);
        Assert.True(h.ViewModel.IsConnected);
    }

    [Fact]
    public async Task A_busy_retry_is_tried_again_on_the_next_poll_inside_the_same_window()
    {
        var calls = 0;
        var h = await Harness.CreateAsync((_, _, _) => Task.FromResult(++calls switch
        {
            1 => new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted"),
            2 => new ConnectionAttemptResult(ConnectionAttemptKind.Busy, "a read is running"),
            _ => new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted"),
        }));
        h.ConnectedAfter = TimeSpan.FromSeconds(12);

        var connect = h.ViewModel.ConnectAsync();
        await h.RunToCompletionAsync(connect, TimeSpan.FromMilliseconds(400), maxSteps: 100);

        Assert.Equal(3, h.Controller.ReconnectCalls);
        Assert.True(h.ViewModel.IsConnected);
    }

    [Fact]
    public async Task Changing_the_selection_during_the_wait_stops_the_retry()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted, twoDevices: true);

        var connect = h.ViewModel.ConnectAsync();
        for (var i = 0; i < 5; i++) await h.StepAsync(TimeSpan.FromMilliseconds(400), connect);
        Assert.Equal(1, h.Controller.ReconnectCalls);

        h.ViewModel.SelectedDeviceId = "device-b";
        await TestSupport.WaitUntil(() => connect.IsCompleted);
        for (var i = 0; i < 40; i++) h.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Delay(50);

        Assert.Equal(1, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
    }

    [Fact]
    public async Task Clearing_the_selection_during_a_busy_read_sends_no_request_once_the_read_finishes()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);
        h.Controller.IsBusy = true;

        var connect = h.ViewModel.ConnectAsync();
        for (var i = 0; i < 3; i++) await h.StepAsync(TimeSpan.FromMilliseconds(100), connect);
        Assert.True(h.ViewModel.IsConnecting);

        h.ViewModel.ClearSelection();
        h.Controller.IsBusy = false; // the read finishes
        await TestSupport.WaitUntil(() => connect.IsCompleted);
        for (var i = 0; i < 10; i++) h.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Delay(50);

        Assert.Equal(0, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.False(h.ViewModel.HasSelectedDevice);
        Assert.DoesNotContain("Windows is busy", h.ViewModel.DetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("Asking Windows", h.ViewModel.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_the_selection_during_the_wait_stops_the_retry()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);

        var connect = h.ViewModel.ConnectAsync();
        for (var i = 0; i < 5; i++) await h.StepAsync(TimeSpan.FromMilliseconds(400), connect);
        Assert.Equal(1, h.Controller.ReconnectCalls);

        h.ViewModel.ClearSelection();
        await TestSupport.WaitUntil(() => connect.IsCompleted);
        for (var i = 0; i < 40; i++) h.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Delay(50);

        Assert.Equal(1, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.DoesNotContain("Not connected yet", h.ViewModel.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_selection_during_a_busy_read_sends_no_request_for_either_device()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted, twoDevices: true);
        h.Controller.IsBusy = true;

        var connect = h.ViewModel.ConnectAsync();
        for (var i = 0; i < 3; i++) await h.StepAsync(TimeSpan.FromMilliseconds(100), connect);

        h.ViewModel.SelectedDeviceId = "device-b";
        h.Controller.IsBusy = false;
        await TestSupport.WaitUntil(() => connect.IsCompleted);
        for (var i = 0; i < 10; i++) h.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Delay(50);

        Assert.Equal(0, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
    }

    [Fact]
    public async Task Disposing_during_the_wait_stops_the_retry()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);

        var connect = h.ViewModel.ConnectAsync();
        for (var i = 0; i < 5; i++) await h.StepAsync(TimeSpan.FromMilliseconds(400), connect);
        h.ViewModel.Dispose();
        await TestSupport.WaitUntil(() => connect.IsCompleted);
        for (var i = 0; i < 40; i++) h.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Delay(50);

        Assert.Equal(1, h.Controller.ReconnectCalls);
    }

    [Fact]
    public async Task A_click_while_already_connecting_is_ignored_and_logged()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted);
        var first = h.ViewModel.ConnectAsync();
        Assert.True(h.ViewModel.IsConnecting);

        await h.ViewModel.ConnectAsync();

        Assert.Equal(1, h.Controller.ReconnectCalls);
        Assert.Contains(h.Logs, line => line == "Connect: click ignored (already connecting).");
        Assert.Equal(1, h.Logs.Count(line => line == "Connect: click accepted."));
        h.ViewModel.Dispose();
        await TestSupport.WaitUntil(() => first.IsCompleted);
    }

    [Fact]
    public async Task Auto_connect_never_retries_and_keeps_its_own_8_second_give_up_and_message()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted, autoConnect: true);

        h.ViewModel.NotifyNearbyEpisode(true);
        Assert.Equal(1, h.Controller.ReconnectCalls);
        Assert.True(h.ViewModel.IsConnecting);
        for (var i = 0; i < 30 && h.ViewModel.IsConnecting; i++)
            await h.StepAsync(TimeSpan.FromMilliseconds(400), () => !h.ViewModel.IsConnecting);

        Assert.Equal(1, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
        Assert.Equal("Windows tried to connect, but couldn't confirm it yet.", h.ViewModel.DetailText);
        Assert.InRange(h.Elapsed, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9));
        // _rearmRequired still applies: a second nearby episode right away does not auto-connect.
        h.ViewModel.NotifyNearbyEpisode(false);
        h.ViewModel.NotifyNearbyEpisode(true);
        Assert.Equal(1, h.Controller.ReconnectCalls);
    }

    [Fact]
    public async Task Auto_connect_does_not_queue_behind_a_running_read()
    {
        var h = await Harness.CreateAsync(AlwaysAttempted, autoConnect: true);
        h.Controller.IsBusy = true;

        h.ViewModel.NotifyNearbyEpisode(true);

        Assert.Equal(0, h.Controller.ReconnectCalls);
        Assert.False(h.ViewModel.IsConnecting);
    }

    private sealed class Harness
    {
        private int _timers;
        public required CountingClock Clock { get; init; }
        public required ScriptedController Controller { get; init; }
        public required CompanionConnectionViewModel ViewModel { get; init; }
        public required List<string> Logs { get; init; }
        public required DateTimeOffset Start { get; init; }

        /// <summary>When set, snapshots report the AirPods connected once this much fake time has passed.</summary>
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

        /// <summary>Advance fake time, then wait until the connect code has parked on its next delay (or finished).</summary>
        public async Task StepAsync(TimeSpan span, Task connect)
            => await StepAsync(span, () => connect.IsCompleted);

        public async Task StepAsync(TimeSpan span, Func<bool> done)
        {
            var before = Volatile.Read(ref Clock.Timers);
            Clock.Advance(span);
            await TestSupport.WaitUntil(() => done() || Volatile.Read(ref Clock.Timers) > before);
        }

        public async Task RunToCompletionAsync(Task connect, TimeSpan step, int maxSteps = 100)
        {
            for (var i = 0; i < maxSteps && !connect.IsCompleted; i++) await StepAsync(step, connect);
            await connect;
        }
    }

    /// <summary>Fake clock that counts the timers (Task.Delay delays) registered on it.</summary>
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
        private int _inFlight;
        private int _maxInFlight;

        public Func<CancellationToken, Task<ConnectionSnapshot>>? Read { get; set; }
        public Func<string, string, CancellationToken, Task<ConnectionAttemptResult>>? Reconnect { get; set; }
        public bool IsBusy { get; set; }
        public int ReconnectCalls => Volatile.Read(ref _reconnectCalls);
        public int MaxConcurrentRequests => Volatile.Read(ref _maxInFlight);

        public event EventHandler? Changed { add { } remove { } }

        public Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
            => Read!(cancellationToken);

        public async Task<ConnectionAttemptResult> ReconnectAsync(
            string deviceId, string expectedContainerId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reconnectCalls);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref _maxInFlight)) < now
                && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
            try { return await Reconnect!(deviceId, expectedContainerId, cancellationToken); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }
}
