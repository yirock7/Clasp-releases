using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Tests;

internal static class TestSupport
{
    /// <summary>Demo scenario with no artificial delays, so tests do not depend on wall-clock time.</summary>
    public static DemoScenario Instant() => new()
    {
        CommandLatency = TimeSpan.Zero,
        ReadbackDelay = TimeSpan.Zero,
        ConnectionStepDelay = TimeSpan.Zero,
    };

    /// <summary>
    /// The demo publishes readbacks from a background task; wait (bounded) for the effect
    /// instead of sleeping a fixed time.
    /// </summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition not reached in time.");
            await Task.Delay(10);
        }
    }
}

/// <summary>Transport whose behaviour each test scripts directly.</summary>
internal sealed class ScriptedTransport : IAirPodsTransport
{
    private long _sequence;

    public ScriptedTransport(string deviceId = "dev") => DeviceId = deviceId;

    public string DeviceId { get; }

    public Func<DeviceCommand, CommandResult> OnSend { get; set; }
        = c => new CommandResult(c.CommandId, CommandOutcome.Acknowledged, DataOrigin.Hardware);

    public TransportInfo Info { get; } = new("Scripted", DataOrigin.Hardware, HubContract.Version, "test");

    public DeviceState? Current { get; private set; }

    public event EventHandler<DeviceState>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>When set, used instead of <see cref="OnSend"/> so a test can hold a command in flight.</summary>
    public Func<DeviceCommand, Task<CommandResult>>? OnSendAsync { get; set; }

    public Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
        => OnSendAsync is { } send ? send(command) : Task.FromResult(OnSend(command));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Publish a connected state with the given mode (and optional overrides). Pass null for
    /// <paramref name="awareness"/> or <paramref name="strength"/> to report them as unknown;
    /// pass <paramref name="link"/> for a state that is not connected.
    /// </summary>
    public DeviceState Publish(
        ListeningMode? mode, DateTimeOffset at, string? deviceId = null, long? sequence = null,
        bool? awareness = false, int? strength = 50, LinkState? link = null)
    {
        var on = Observation<bool>.At(true, at);
        var state = new DeviceState(
            deviceId ?? DeviceId, "Test buds", "AirPods Pro 3", DataOrigin.Hardware, sequence ?? ++_sequence, at,
            // ConnectControl: this transport answers Connect/Disconnect (contract 1.1).
            DeviceCapabilities.Battery | DeviceCapabilities.ListeningModes | DeviceCapabilities.ConversationAwareness | DeviceCapabilities.AdaptiveStrength
                | DeviceCapabilities.ConnectControl,
            link ?? new LinkState(on, Observation<bool>.At(false, at), on, on),
            new BatteryState(BatteryComponent.From(50, false, at), BatteryComponent.From(50, false, at), BatteryComponent.Unknown),
            new ListeningState(mode is { } m ? Observation<ListeningMode>.At(m, at) : default,
                new[] { ListeningMode.Off, ListeningMode.Transparency, ListeningMode.Adaptive, ListeningMode.NoiseCancellation }),
            awareness is bool aw ? Observation<bool>.At(aw, at) : default,
            strength is int st ? Observation<int>.At(st, at) : default);
        Current = state;
        StateChanged?.Invoke(this, state);
        return state;
    }
}
