namespace AppleDeviceHub.Core.Contracts;

/// <summary>
/// Version of the local device contract in this namespace (records, enums, and
/// <see cref="IAirPodsTransport"/>). Bump <see cref="Major"/> for a breaking change and
/// <see cref="Minor"/> for an additive one.
/// <list type="bullet">
/// <item>1.0 (phase one): states, observations, mode/awareness/strength commands.</item>
/// <item>1.1 (hardware-readiness packet, additive): <see cref="ConnectCommand"/> and
/// <see cref="DisconnectCommand"/> (added in code by the Clasp redesign, f7b802b, without a
/// version bump; 1.1 records them), and the capability flags
/// <see cref="DeviceCapabilities.ConnectControl"/> and
/// <see cref="DeviceCapabilities.ControlSessionOnly"/>. A 1.0 transport simply does not set
/// the new flags, so the UI offers no connect action for it.</item>
/// <item>1.2 (everyday-features packet, additive): <see cref="DataOrigin.Broadcast"/>,
/// <see cref="DeviceCapabilities.BroadcastObservation"/> and
/// ear observations under the existing <see cref="DeviceCapabilities.EarDetection"/>, and the optional <see cref="DeviceState.Ears"/>
/// property (defaults to unknown, so 1.1 transports need no change).</item>
/// <item>1.3 (model-aware battery, additive): the optional <see cref="DeviceState.Layout"/> property
/// (defaults to earbuds with a case) and <see cref="DeviceState.DeviceNoun"/> (defaults to "AirPods"),
/// so 1.2 transports need no change.</item>
/// <item>1.4 (model artwork, additive): the optional <see cref="DeviceState.ModelId"/> property (defaults to null,
/// meaning no model is known), so 1.3 transports need no change.</item>
/// </list>
/// The engine wire protocol (windows/protocol/clasp-engine-v1) is versioned separately.
/// </summary>
public static class HubContract
{
    public const int Major = 1;
    public const int Minor = 4;

    public static string Version => $"{Major}.{Minor}";
}

/// <summary>Where a value came from. Every UI surface must show where its data came from.</summary>
public enum DataOrigin
{
    /// <summary>No source has reported this value.</summary>
    Unknown = 0,

    /// <summary>
    /// Synthetic values from the demo transport. Any surface showing them must carry a
    /// visible "Demo" label, including simulated command results.
    /// </summary>
    Demo = 1,

    /// <summary>
    /// Reported by a hardware engine (for example the Clasp engine). Still subject to
    /// freshness checks, and not by itself proof that a feature works on the user's AirPods.
    /// </summary>
    Hardware = 2,

    /// <summary>
    /// Contract 1.2: decoded from a Bluetooth LE broadcast (Apple proximity message) heard
    /// nearby, without a connection. Values are approximate (battery in 10 % steps) and the
    /// sender's identity is NOT cryptographically confirmed to be the user's AirPods.
    /// </summary>
    Broadcast = 3,
}

/// <summary>How recent a reported value is, relative to a caller-chosen maximum age.</summary>
public enum Freshness
{
    /// <summary>Never reported. Render as unknown, never as a guessed value.</summary>
    Unknown = 0,
    Fresh = 1,

    /// <summary>Reported, but older than the allowed age. Render with its age, de-emphasised.</summary>
    Stale = 2,
}

/// <summary>
/// One reported value plus the time it was observed. <c>Value == null</c> means the source
/// has not reported it; consumers must show that as unknown (never as 0 or "Off").
/// </summary>
public readonly record struct Observation<T>(T? Value, DateTimeOffset? ObservedAt)
    where T : struct
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Frozen contract v1.0 factory; Observation<int>.Unknown reads clearly at call sites.")]
    public static Observation<T> Unknown => default;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Frozen contract v1.0 factory.")]
    public static Observation<T> At(T value, DateTimeOffset observedAt) => new(value, observedAt);

    public bool HasValue => Value.HasValue && ObservedAt.HasValue;

    public Freshness FreshnessAt(DateTimeOffset now, TimeSpan maxAge)
    {
        if (!HasValue) return Freshness.Unknown;
        return now - ObservedAt!.Value > maxAge ? Freshness.Stale : Freshness.Fresh;
    }
}
