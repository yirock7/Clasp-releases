using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.Core.Tests.Connection;

public class RepairSequenceTests
{
    private const string DeviceId = "dev-1";
    private const string Container = "11111111-2222-3333-4444-555555555555";

    private static readonly RepairOptions Fast = new()
    {
        TurnOffTimeout = TimeSpan.FromMilliseconds(80),
        SettleDelay = TimeSpan.Zero,
        TurnOnTimeout = TimeSpan.FromMilliseconds(80),
        DeviceReadyTimeout = TimeSpan.FromMilliseconds(80),
        ReconnectTimeout = TimeSpan.FromMilliseconds(80),
        ReconnectVerifyTimeout = TimeSpan.FromMilliseconds(80),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    private static Task Delay(TimeSpan span, CancellationToken token) => Task.Delay(span, token);

    private static RepairSequence Make(FakeRadio radio, FakeConnection connection)
        => new(radio, connection, Fast, Delay);

    [Fact]
    public async Task Happy_path_restarts_radio_and_reports_success_in_order()
    {
        var radio = new FakeRadio();
        var connection = new FakeConnection();
        var sequence = Make(radio, connection);
        var steps = new List<RepairStep>();
        sequence.ProgressChanged += (_, p) => steps.Add(p.Step);

        var result = await sequence.RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal("Bluetooth restarted and your AirPods reconnected.", result.Message);
        Assert.Equal(new[] { false, true }, radio.Requests);
        Assert.Equal(1, connection.ReconnectCalls);
        Assert.Equal(new[]
        {
            RepairStep.TurningOff, RepairStep.Settling, RepairStep.TurningOn,
            RepairStep.WaitingForDevice, RepairStep.Reconnecting, RepairStep.Finished,
        }, steps);
        Assert.False(sequence.IsRunning);
    }

    [Theory]
    [InlineData(RadioSwitchOutcome.AccessDenied, RepairOutcome.AccessDenied, "Windows didn't allow Clasp to turn Bluetooth off.")]
    [InlineData(RadioSwitchOutcome.NoRadio, RepairOutcome.NoRadio, "Clasp couldn't find a Bluetooth radio on this PC.")]
    [InlineData(RadioSwitchOutcome.Busy, RepairOutcome.RadioBusy, "Bluetooth is busy right now. Try again in a moment.")]
    public async Task Refusal_when_reading_state_changes_nothing(RadioSwitchOutcome outcome, RepairOutcome expected, string message)
    {
        var radio = new FakeRadio { StateOutcome = outcome };
        var result = await Make(radio, new FakeConnection()).RunAsync(DeviceId, Container);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(message, result.Message);
        Assert.Empty(radio.Requests);
    }

    [Fact]
    public async Task Access_denied_when_switching_off_does_not_try_to_turn_on_if_radio_still_on()
    {
        var radio = new FakeRadio { OffOutcome = RadioSwitchOutcome.AccessDenied };
        var result = await Make(radio, new FakeConnection()).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.AccessDenied, result.Outcome);
        Assert.Equal(new[] { false }, radio.Requests);
    }

    [Fact]
    public async Task Radio_that_is_already_off_is_left_alone()
    {
        var radio = new FakeRadio { IsOn = false };
        var result = await Make(radio, new FakeConnection()).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.RadioWasOff, result.Outcome);
        Assert.Empty(radio.Requests);
    }

    [Fact]
    public async Task Turn_off_timeout_reports_and_makes_sure_radio_is_on()
    {
        var radio = new FakeRadio { HangOff = true };
        var result = await Make(radio, new FakeConnection()).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.TurnOffTimedOut, result.Outcome);
        Assert.True(radio.IsOn); // never went off, so no extra "on" request was needed
        Assert.Single(radio.Requests);
    }

    [Fact]
    public async Task Turn_on_timeout_retries_once_to_restore_but_does_not_loop()
    {
        var radio = new FakeRadio { HangOnFirstTime = true };
        var result = await Make(radio, new FakeConnection()).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.TurnOnTimedOut, result.Outcome);
        Assert.Equal(new[] { false, true, true }, radio.Requests);
        Assert.True(radio.IsOn);
    }

    [Fact]
    public async Task Failure_to_turn_radio_back_on_is_reported_as_stuck_off()
    {
        var radio = new FakeRadio { OnOutcome = RadioSwitchOutcome.Failed };
        var result = await Make(radio, new FakeConnection()).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.RadioStuckOff, result.Outcome);
        Assert.Contains("Windows Settings", result.Message);
    }

    [Fact]
    public async Task Device_that_never_reappears_times_out_without_reconnecting()
    {
        var connection = new FakeConnection { DeviceListed = false };
        var radio = new FakeRadio();
        var result = await Make(radio, connection).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.DeviceNotReady, result.Outcome);
        Assert.Equal(0, connection.ReconnectCalls);
        Assert.True(radio.IsOn);
    }

    [Fact]
    public async Task Reconnect_timeout_gives_the_take_out_of_case_message()
    {
        var connection = new FakeConnection { HangReconnect = true };
        var result = await Make(new FakeRadio(), connection).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.ReconnectFailed, result.Outcome);
        Assert.Equal(
            "Bluetooth restarted, but the AirPods didn't reconnect. Take them out of the case and try Connect.",
            result.Message);
    }

    [Theory]
    [InlineData(ConnectionAttemptKind.Failed)]
    [InlineData(ConnectionAttemptKind.Busy)]
    [InlineData(ConnectionAttemptKind.Unsupported)]
    public async Task Reconnect_refusal_reports_failure_and_never_retries(ConnectionAttemptKind kind)
    {
        var connection = new FakeConnection { AttemptKind = kind };
        var result = await Make(new FakeRadio(), connection).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.ReconnectFailed, result.Outcome);
        Assert.Equal(1, connection.ReconnectCalls);
    }

    [Fact]
    public async Task Attempt_accepted_but_device_never_connects_is_a_failure()
    {
        var connection = new FakeConnection { ConnectsAfterReconnect = false };
        var result = await Make(new FakeRadio(), connection).RunAsync(DeviceId, Container);

        Assert.Equal(RepairOutcome.ReconnectFailed, result.Outcome);
        Assert.Equal(1, connection.ReconnectCalls);
    }

    [Fact]
    public async Task Cancel_while_radio_is_off_turns_radio_back_on()
    {
        var radio = new FakeRadio();
        using var cts = new CancellationTokenSource();
        var sequence = new RepairSequence(radio, new FakeConnection(), Fast, async (span, token) =>
        {
            cts.Cancel(); // user cancels during the settle delay, radio is off at this point
            await Task.Delay(span, token);
        });

        var result = await sequence.RunAsync(DeviceId, Container, cts.Token);

        Assert.Equal(RepairOutcome.Cancelled, result.Outcome);
        Assert.True(radio.IsOn);
        Assert.True(radio.Requests[^1]);
        Assert.False(sequence.IsRunning);
    }

    [Fact]
    public async Task Cancel_during_wait_for_device_still_leaves_radio_on()
    {
        var radio = new FakeRadio();
        var connection = new FakeConnection { DeviceListed = false };
        using var cts = new CancellationTokenSource();
        connection.OnSnapshot = () => cts.Cancel();

        var result = await Make(radio, connection).RunAsync(DeviceId, Container, cts.Token);

        Assert.Equal(RepairOutcome.Cancelled, result.Outcome);
        Assert.True(radio.IsOn);
    }

    [Fact]
    public async Task Second_run_while_first_is_active_is_rejected_without_touching_the_radio()
    {
        var radio = new FakeRadio { GateOff = new TaskCompletionSource() };
        var sequence = new RepairSequence(radio, new FakeConnection(), new RepairOptions
        {
            TurnOffTimeout = TimeSpan.FromSeconds(30),
            SettleDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(5),
        }, Delay);

        var first = sequence.RunAsync(DeviceId, Container);
        await radio.OffRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sequence.IsRunning);

        var second = await sequence.RunAsync(DeviceId, Container);
        Assert.Equal(RepairOutcome.AlreadyRunning, second.Outcome);
        Assert.Equal(new[] { false }, radio.Requests);

        radio.GateOff.SetResult();
        var firstResult = await first;
        Assert.Equal(RepairOutcome.Success, firstResult.Outcome);
        Assert.False(sequence.IsRunning);

        var third = await sequence.RunAsync(DeviceId, Container);
        Assert.Equal(RepairOutcome.Success, third.Outcome);
    }

    [Fact]
    public async Task RunningChanged_fires_on_start_and_end()
    {
        var sequence = Make(new FakeRadio(), new FakeConnection());
        var states = new List<bool>();
        sequence.RunningChanged += (_, _) => states.Add(sequence.IsRunning);

        await sequence.RunAsync(DeviceId, Container);

        Assert.Equal(new[] { true, false }, states);
    }

    private sealed class FakeRadio : IBluetoothRadioSwitch
    {
        public bool IsOn { get; set; } = true;
        public List<bool> Requests { get; } = new();
        public RadioSwitchOutcome StateOutcome { get; init; } = RadioSwitchOutcome.Success;
        public RadioSwitchOutcome OffOutcome { get; init; } = RadioSwitchOutcome.Success;
        public RadioSwitchOutcome OnOutcome { get; init; } = RadioSwitchOutcome.Success;
        public bool HangOff { get; init; }
        public bool HangOnFirstTime { get; init; }
        public TaskCompletionSource? GateOff { get; init; }
        public TaskCompletionSource OffRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hungOnce;

        public Task<RadioStateResult> GetStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RadioStateResult(StateOutcome, IsOn ? BluetoothRadioState.On : BluetoothRadioState.Off));

        public async Task<RadioSwitchResult> SetStateAsync(bool turnOn, CancellationToken cancellationToken = default)
        {
            Requests.Add(turnOn);
            if (!turnOn)
            {
                OffRequested.TrySetResult();
                if (GateOff is not null) await GateOff.Task.WaitAsync(cancellationToken);
                if (HangOff) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (OffOutcome == RadioSwitchOutcome.Success) IsOn = false;
                return new(OffOutcome);
            }
            if (HangOnFirstTime && !_hungOnce)
            {
                _hungOnce = true;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (OnOutcome == RadioSwitchOutcome.Success) IsOn = true;
            return new(OnOutcome);
        }
    }

    private sealed class FakeConnection : IAirPodsConnectionController
    {
        public bool DeviceListed { get; init; } = true;
        public bool HangReconnect { get; init; }
        public bool ConnectsAfterReconnect { get; init; } = true;
        public ConnectionAttemptKind AttemptKind { get; init; } = ConnectionAttemptKind.Attempted;
        public Action? OnSnapshot { get; set; }
        public int ReconnectCalls { get; private set; }
        private bool _reconnected;

        public event EventHandler? Changed { add { } remove { } }
        public bool IsBusy => false;

        public Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
        {
            OnSnapshot?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var devices = DeviceListed
                ? new[]
                {
                    new ConnectionDevice(DeviceId, "AirPods", Container, true,
                        IsConnected: _reconnected && ConnectsAfterReconnect, false, true, null),
                }
                : Array.Empty<ConnectionDevice>();
            return Task.FromResult(new ConnectionSnapshot(true, devices, Array.Empty<ConnectionAudioEndpoint>(), null));
        }

        public async Task<ConnectionAttemptResult> ReconnectAsync(string deviceId, string expectedContainerId,
            CancellationToken cancellationToken = default)
        {
            ReconnectCalls++;
            if (HangReconnect) await Task.Delay(Timeout.Infinite, cancellationToken);
            _reconnected = AttemptKind == ConnectionAttemptKind.Attempted;
            return new(AttemptKind, "fake");
        }
    }
}
