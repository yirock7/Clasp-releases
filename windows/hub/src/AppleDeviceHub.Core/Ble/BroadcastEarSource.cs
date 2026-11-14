using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Ble;

/// <summary>A broadcast ear placement that passed the identity, freshness and stability gate.</summary>
/// <param name="Primary">Placement of the AirPods' current primary bud (roles change; not left/right).</param>
/// <param name="Secondary">Placement of the secondary bud.</param>
/// <param name="ObservedAt">When the confirming (latest) sighting was heard.</param>
public sealed record BroadcastEarReading(PodPlacement Primary, PodPlacement Secondary, DateTimeOffset ObservedAt)
{
    public int InEarCount => (Primary == PodPlacement.InEar ? 1 : 0) + (Secondary == PodPlacement.InEar ? 1 : 0);
}

public enum BroadcastEarVerdict
{
    /// <summary>The gate failed (setting off, identity, connection, stale, no ear bits). Nothing may act.</summary>
    Rejected = 0,

    /// <summary>A usable but not yet stable reading (jitter filter still running). Nothing may act yet.</summary>
    Pending = 1,

    /// <summary>A stable, fresh reading from probably-your AirPods.</summary>
    Stable = 2,
}

public readonly record struct BroadcastEarResult(BroadcastEarVerdict Verdict, BroadcastEarReading? Reading, string Reason)
{
    public static BroadcastEarResult Rejected(string reason) => new(BroadcastEarVerdict.Rejected, null, reason);
}

/// <summary>
/// Turns broadcast <see cref="DeviceState"/>s into stable ear readings that may drive automatic
/// actions (owner decision 2026-10-20: opt-in "Ear detection (nearby estimate)", off by default).
/// A reading is released only when ALL of these hold:
/// <list type="bullet">
/// <item>the setting <see cref="Enabled"/> is on;</item>
/// <item>the state is of <see cref="DataOrigin.Broadcast"/> and carries both ear bits;</item>
/// <item>the source's <see cref="IdentityConfidence"/> for the selected paired AirPods is LikelyYours,
/// meaning an independent Windows timing or battery clue also supports the match;</item>
/// <item>the AirPods are connected in Windows;</item>
/// <item>the reading is fresh (latest sighting at most <see cref="MaxAge"/> old);</item>
/// <item>the reading is stable: the same placement in at least <see cref="MinSightings"/> distinct
/// sightings spanning at least <see cref="StableFor"/> (absorbs jitter, notably from H2-chip AirPods).</item>
/// </list>
/// Any gate failure also clears the stability tracking, so evidence must be re-earned afterwards.
/// A sighting is one distinct ear observation time; republishing the same observation (for example
/// because the identity rating changed) counts once. Not thread-safe: callers serialise access.
/// </summary>
public sealed class BroadcastEarSource
{
    private PodPlacement? _candidatePrimary;
    private PodPlacement? _candidateSecondary;
    private DateTimeOffset _firstAt;
    private DateTimeOffset _lastAt;
    private int _count;

    /// <summary>User setting "Ear detection (nearby estimate)". Off by default.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Reset();
        }
    }

    private bool _enabled;

    /// <summary>The same placement must persist at least this long across sightings.</summary>
    public TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The same placement must be seen in at least this many distinct sightings.</summary>
    public int MinSightings { get; set; } = 2;

    /// <summary>The latest sighting may be at most this old (matches <c>EarMediaPolicy.MaxEarEventAge</c>).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Forget the stability tracking (sleep, transport change, disconnect).</summary>
    public void Reset()
    {
        _candidatePrimary = null;
        _candidateSecondary = null;
        _count = 0;
    }

    /// <summary>
    /// Evaluate one broadcast state. <paramref name="identity"/> is
    /// <c>BroadcastObservationTransport.IdentityConfidence</c> for the selected paired AirPods and
    /// <paramref name="windowsConnected"/> whether Windows reports them connected.
    /// </summary>
    public BroadcastEarResult Evaluate(DeviceState state, IdentityConfidence identity, bool windowsConnected, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_enabled) return Reject("ear detection from nearby broadcasts is off");
        if (state.Origin != DataOrigin.Broadcast) return Reject("not a broadcast state");
        if (!identity.HasIndependentClue()) return Reject("no independent pairing clue supports this broadcast");
        if (!windowsConnected) return Reject("AirPods not connected in Windows");
        if (!state.Supports(DeviceCapabilities.EarDetection)) return Reject("ear bits unavailable");

        var a = state.Ears.Primary;
        var b = state.Ears.Secondary;
        if (!a.HasValue || !b.HasValue) return Reject("ear bits unavailable");
        var primary = a.Value!.Value;
        var secondary = b.Value!.Value;
        if (primary == PodPlacement.Disconnected || secondary == PodPlacement.Disconnected)
            return Reject("a bud is disconnected from the other");

        var at = a.ObservedAt!.Value > b.ObservedAt!.Value ? a.ObservedAt!.Value : b.ObservedAt!.Value;
        if (now - at > MaxAge) return Reject("broadcast ear reading is stale");

        if (_candidatePrimary == primary && _candidateSecondary == secondary)
        {
            if (at > _lastAt)
            {
                _count++;
                _lastAt = at;
            }
            else if (at < _firstAt)
            {
                return Reject("broadcast ear reading is out of order");
            }
        }
        else
        {
            _candidatePrimary = primary;
            _candidateSecondary = secondary;
            _firstAt = at;
            _lastAt = at;
            _count = 1;
        }

        if (_count < MinSightings || _lastAt - _firstAt < StableFor)
            return new BroadcastEarResult(BroadcastEarVerdict.Pending, null, "waiting for a stable broadcast ear reading");

        return new BroadcastEarResult(BroadcastEarVerdict.Stable, new BroadcastEarReading(primary, secondary, _lastAt), "stable");
    }

    private BroadcastEarResult Reject(string reason)
    {
        Reset();
        return BroadcastEarResult.Rejected(reason);
    }
}
