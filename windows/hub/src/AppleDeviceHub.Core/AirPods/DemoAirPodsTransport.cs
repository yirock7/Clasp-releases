using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.AirPods;

/// <summary>Battery situations the demo can show, so unknown/stale/low rendering can be inspected.</summary>
public enum DemoBatteryScenario
{
    Normal = 0,
    Low = 1,
    Unknown = 2,
    Stale = 3,
}

/// <summary>Ear placement the demo reports once connected, so the buds-out hero drawing can be inspected. Unknown (the default) reports none.</summary>
public enum DemoEarScenario
{
    Unknown = 0,

    /// <summary>Both buds in the ears.</summary>
    InEar = 1,

    /// <summary>Both buds out of the case and out of the ears.</summary>
    OutOfEar = 2,

    /// <summary>Only the left bud is out (in an ear); the right one is still in the case.</summary>
    LeftOnly = 3,

    /// <summary>Both buds in the case.</summary>
    InCase = 4,
}

/// <summary>Knobs for the demo. Read on each command, so the UI can change them at runtime.</summary>
public sealed class DemoScenario
{
    /// <summary>One-shot: the next command fails with a simulated, clearly labelled error.</summary>
    public bool FailNextCommand { get; set; }

    /// <summary>Simulated engine round trip before the command result.</summary>
    public TimeSpan CommandLatency { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>Simulated delay between acknowledgement and the device reporting the new value.</summary>
    public TimeSpan ReadbackDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Pause between the steps of the simulated case-open story.</summary>
    public TimeSpan ConnectionStepDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    public DemoBatteryScenario Battery { get; set; } = DemoBatteryScenario.Normal;

    /// <summary>Ear placement reported while connected (screenshots only; the default reports none, as before).</summary>
    public DemoEarScenario Ears { get; set; } = DemoEarScenario.Unknown;

    /// <summary>
    /// Wire model id the demo pretends to be. Null keeps today's AirPods Pro 3 (0x2720). Set it when the demo is
    /// created: the state is built from it once, on first publish.
    /// </summary>
    public ushort? ModelId { get; set; }
}

/// <summary>
/// A deliberately synthetic AirPods Pro 3. It starts no daemon, opens no driver, and uses no
/// Bluetooth API: it only produces <see cref="DeviceState"/> records stamped
/// <see cref="DataOrigin.Demo"/>. Simulated command results say "Demo" in their detail text.
/// </summary>
public sealed class DemoAirPodsTransport : IAirPodsTransport
{
    public const string DemoDeviceId = "demo:airpods-pro-3";

    private static readonly IReadOnlyList<ListeningMode> SupportedModes = new[]
    {
        ListeningMode.Off, ListeningMode.Transparency, ListeningMode.Adaptive, ListeningMode.NoiseCancellation,
    };

    private const DeviceCapabilities DemoCapabilities =
        DeviceCapabilities.Battery | DeviceCapabilities.ListeningModes |
        DeviceCapabilities.ConversationAwareness | DeviceCapabilities.AdaptiveStrength |
        DeviceCapabilities.CaseLidEvents | DeviceCapabilities.ConnectControl;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _sequence;
    private DeviceState? _current;

    public DemoAirPodsTransport(DemoScenario? scenario = null, TimeProvider? time = null)
    {
        Scenario = scenario ?? new DemoScenario();
        _time = time ?? TimeProvider.System;
    }

    public DemoScenario Scenario { get; }

    public TransportInfo Info { get; } = new(
        "Demo transport",
        DataOrigin.Demo,
        HubContract.Version,
        "Synthetic AirPods Pro 3 data. No daemon, driver, or Bluetooth device is used.");

    public DeviceState? Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<DeviceState>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
        => PlayCaseOpenAsync(cancellationToken);

    /// <summary>
    /// Simulate opening the case next to the PC: Nearby, then Connecting, then Connected with
    /// battery and mode. Each step is a separate state so presentation logic sees real transitions.
    /// </summary>
    public async Task PlayCaseOpenAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var ct = linked.Token;

        Publish((s, now) => s with
        {
            Link = new LinkState(Observation<bool>.At(true, now), Observation<bool>.At(false, now),
                Observation<bool>.At(false, now), Observation<bool>.At(false, now)),
            Battery = DemoBattery(now),
        });
        await Task.Delay(Scenario.ConnectionStepDelay, _time, ct).ConfigureAwait(false);

        Publish((s, now) => s with
        {
            Link = s.Link with { Connecting = Observation<bool>.At(true, now) },
        });
        await Task.Delay(Scenario.ConnectionStepDelay, _time, ct).ConfigureAwait(false);

        Publish((s, now) => s with
        {
            Link = new LinkState(Observation<bool>.At(true, now), Observation<bool>.At(false, now),
                Observation<bool>.At(true, now), Observation<bool>.At(true, now)),
            Battery = DemoBattery(now),
            Ears = DemoEars(now),
            Listening = s.Listening.Actual.HasValue
                ? s.Listening
                : new ListeningState(Observation<ListeningMode>.At(ListeningMode.NoiseCancellation, now), SupportedModes),
            ConversationAwareness = s.ConversationAwareness.HasValue ? s.ConversationAwareness : Observation<bool>.At(true, now),
            AdaptiveStrength = s.AdaptiveStrength.HasValue ? s.AdaptiveStrength : Observation<int>.At(50, now),
        });
    }

    /// <summary>Simulate the buds going out of range: everything reports "not connected".</summary>
    public void SimulateOutOfRange()
        => Publish((s, now) => s with
        {
            Link = new LinkState(Observation<bool>.At(false, now), Observation<bool>.At(false, now),
                Observation<bool>.At(false, now), Observation<bool>.At(false, now)),
        });

    /// <summary>Simulate a mode change made on the earbud stem or iPhone, not from this app.</summary>
    public void SimulateExternalModeChange(ListeningMode mode)
        => Publish((s, now) => s with
        {
            Listening = s.Listening with { Actual = Observation<ListeningMode>.At(mode, now) },
        });

    /// <summary>Re-publish battery values for the current <see cref="DemoScenario.Battery"/>.</summary>
    public void RefreshBattery()
        => Publish((s, now) => s with { Battery = DemoBattery(now) });

    public async Task<CommandResult> SendAsync(DeviceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await Task.Delay(Scenario.CommandLatency, _time, cancellationToken).ConfigureAwait(false);

        bool fail;
        lock (_gate)
        {
            fail = Scenario.FailNextCommand;
            Scenario.FailNextCommand = false;
        }
        if (fail)
            return Result(command, CommandOutcome.Failed, "Simulated failure (Demo). Nothing was sent to any device.");

        switch (command)
        {
            case ConnectCommand:
                if (Current?.Link.Summarize() == ConnectionSummary.Connected)
                    return Result(command, CommandOutcome.Acknowledged, "Already connected (Demo).");
                if (Current?.Link.Nearby.Value != true)
                    return Result(command, CommandOutcome.TransportUnavailable, "Demo AirPods are not nearby. Open the case near your PC.");
                ScheduleConnect();
                return Result(command, CommandOutcome.Acknowledged, "Connecting (Demo).");

            case DisconnectCommand:
                // The buds stay nearby: only the sessions drop, as when audio moves to another device.
                ScheduleReadback((s, now) => s with
                {
                    Link = new LinkState(Observation<bool>.At(true, now), Observation<bool>.At(false, now),
                        Observation<bool>.At(false, now), Observation<bool>.At(false, now)),
                });
                return Result(command, CommandOutcome.Acknowledged, "Disconnected (Demo).");
        }

        if (Current is not { } state || state.Link.Summarize() != ConnectionSummary.Connected)
            return Result(command, CommandOutcome.TransportUnavailable, "Demo AirPods are not connected.");

        switch (command)
        {
            case SetListeningModeCommand m:
                if (!state.Listening.Supported.Contains(m.Mode))
                    return Result(command, CommandOutcome.Rejected, "Mode not supported (Demo).");
                ScheduleReadback((s, now) => s with
                {
                    Listening = s.Listening with { Actual = Observation<ListeningMode>.At(m.Mode, now) },
                });
                return Result(command, CommandOutcome.Acknowledged, "Accepted by the demo transport.");

            case SetConversationAwarenessCommand c:
                ScheduleReadback((s, now) => s with { ConversationAwareness = Observation<bool>.At(c.Enabled, now) });
                return Result(command, CommandOutcome.Acknowledged, "Accepted by the demo transport.");

            case SetAdaptiveStrengthCommand a:
                if (a.Percent is < 0 or > 100)
                    return Result(command, CommandOutcome.Rejected, "Strength must be 0-100 (Demo).");
                ScheduleReadback((s, now) => s with { AdaptiveStrength = Observation<int>.At(a.Percent, now) });
                return Result(command, CommandOutcome.Acknowledged, "Accepted by the demo transport.");

            default:
                return Result(command, CommandOutcome.Unsupported, "Not supported by the demo transport.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- internals ------------------------------------------------------------------

    private static CommandResult Result(DeviceCommand command, CommandOutcome outcome, string detail)
        => new(command.CommandId, outcome, DataOrigin.Demo, detail);

    private void ScheduleReadback(Func<DeviceState, DateTimeOffset, DeviceState> mutate)
    {
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Scenario.ReadbackDelay, _time, token).ConfigureAwait(false);
                Publish(mutate);
            }
            catch (OperationCanceledException)
            {
                // Transport disposed before the simulated readback; nothing to report.
            }
        }, token);
    }

    /// <summary>Connecting, then Connected after one step delay (the same states the case-open story uses).</summary>
    private void ScheduleConnect()
    {
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Scenario.ReadbackDelay, _time, token).ConfigureAwait(false);
                Publish((s, now) => s with { Link = s.Link with { Connecting = Observation<bool>.At(true, now) } });
                await Task.Delay(Scenario.ConnectionStepDelay, _time, token).ConfigureAwait(false);
                Publish((s, now) => s with
                {
                    Link = new LinkState(Observation<bool>.At(true, now), Observation<bool>.At(false, now),
                        Observation<bool>.At(true, now), Observation<bool>.At(true, now)),
                    Battery = DemoBattery(now),
                    Ears = DemoEars(now),
                });
            }
            catch (OperationCanceledException)
            {
                // Transport disposed mid-connect; nothing to report.
            }
        }, token);
    }

    private void Publish(Func<DeviceState, DateTimeOffset, DeviceState> mutate)
    {
        DeviceState next;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var basis = _current ?? Initial(now);
            next = mutate(basis, now) with
            {
                Origin = DataOrigin.Demo,
                Sequence = ++_sequence,
                ObservedAt = now,
            };
            _current = next;
        }
        StateChanged?.Invoke(this, next);
    }

    private const ushort DefaultModelId = 0x2720; // AirPods Pro 3

    private ushort DemoModelId => Scenario.ModelId ?? DefaultModelId;

    private BatteryLayout DemoLayout => AppleModelCatalog.Find(DemoModelId)?.Layout ?? BatteryLayout.SingleBattery;

    private DeviceState Initial(DateTimeOffset now) => new(
        DeviceId: DemoDeviceId,
        DisplayName: AppleModelCatalog.DisplayNameOf(DemoModelId),
        ModelName: AppleModelCatalog.DisplayNameOf(DemoModelId),
        Origin: DataOrigin.Demo,
        Sequence: 0,
        ObservedAt: now,
        Capabilities: DemoCapabilities,
        Link: LinkState.Unknown,
        Battery: BatteryState.Unknown,
        Listening: new ListeningState(Observation<ListeningMode>.Unknown, SupportedModes),
        ConversationAwareness: Observation<bool>.Unknown,
        AdaptiveStrength: Observation<int>.Unknown)
    {
        ModelId = DemoModelId,
        Layout = DemoLayout,
        DeviceNoun = AppleModelCatalog.FamilyNounOf(DemoModelId),
    };

    /// <summary>The scenario's ear placement, by side as well as primary and secondary (the demo knows which is which).</summary>
    private EarState DemoEars(DateTimeOffset now)
    {
        static Observation<PodPlacement> At(PodPlacement placement, DateTimeOffset at) => Observation<PodPlacement>.At(placement, at);
        (PodPlacement Left, PodPlacement Right)? sides = Scenario.Ears switch
        {
            DemoEarScenario.InEar => (PodPlacement.InEar, PodPlacement.InEar),
            DemoEarScenario.OutOfEar => (PodPlacement.OutOfEar, PodPlacement.OutOfEar),
            DemoEarScenario.LeftOnly => (PodPlacement.InEar, PodPlacement.InCase),
            DemoEarScenario.InCase => (PodPlacement.InCase, PodPlacement.InCase),
            _ => null,
        };
        if (sides is not { } placed) return EarState.Unknown;
        return new EarState(At(placed.Left, now), At(placed.Right, now)) { Left = At(placed.Left, now), Right = At(placed.Right, now) };
    }

    /// <summary>The scenario's levels, trimmed to the batteries this model has: one level for headphones, no case for some buds.</summary>
    private BatteryState DemoBattery(DateTimeOffset now)
    {
        var full = DemoLevels(now);
        return DemoLayout switch
        {
            BatteryLayout.SingleBattery => new BatteryState(full.Left, default, default),
            BatteryLayout.EarbudsNoCaseBattery => full with { Case = default },
            _ => full,
        };
    }

    private BatteryState DemoLevels(DateTimeOffset now) => Scenario.Battery switch
    {
        DemoBatteryScenario.Low => new BatteryState(
            BatteryComponent.From(10, false, now), BatteryComponent.From(10, false, now), BatteryComponent.From(20, false, now)),
        DemoBatteryScenario.Unknown => BatteryState.Unknown,
        DemoBatteryScenario.Stale => new BatteryState(
            BatteryComponent.From(80, false, now - TimeSpan.FromMinutes(12)),
            BatteryComponent.From(70, false, now - TimeSpan.FromMinutes(12)),
            BatteryComponent.From(60, true, now - TimeSpan.FromMinutes(12))),
        _ => new BatteryState(
            BatteryComponent.From(80, false, now), BatteryComponent.From(70, false, now), BatteryComponent.From(60, true, now)),
    };
}
