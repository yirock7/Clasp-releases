using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Battery;

/// <summary>
/// Turns a displayed battery state into a history sample. The display may keep an old value (for
/// example the case level, which AirPods do not report while in the ears); history must not store
/// that old value as if it had just been measured.
/// </summary>
public static class BatterySampling
{
    /// <summary>Components of one broadcast message share a timestamp; this absorbs clock jitter between them.</summary>
    public static readonly TimeSpan SameReadingTolerance = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The sample for <paramref name="battery"/>, timed at its newest component. For a broadcast, a component
    /// is included only when it was measured with that reading (its time within
    /// <see cref="SameReadingTolerance"/> of the newest); an older, retained one is left out (level and
    /// charging null). Other sources report their parts together, so all parts are kept. Null when nothing
    /// is reported.
    /// </summary>
    public static BatterySample? FromState(BatteryState battery, BatterySampleSource source)
    {
        ArgumentNullException.ThrowIfNull(battery);
        DateTimeOffset? newest = null;
        foreach (var part in new[] { battery.Left, battery.Right, battery.Case })
            if (part.Level.HasValue && part.Level.ObservedAt is { } at && (newest is null || at > newest)) newest = at;
        if (newest is not { } time) return null;

        bool Measured(BatteryComponent part)
            => part.Level.HasValue && (source != BatterySampleSource.Broadcast
                || (part.Level.ObservedAt is { } at && time - at <= SameReadingTolerance));
        int? Level(BatteryComponent part) => Measured(part) ? part.Level.Value : null;
        bool? Charging(BatteryComponent part) => Measured(part) && part.Charging.HasValue ? part.Charging.Value : null;

        var sample = new BatterySample(time, Level(battery.Left), Level(battery.Right), Level(battery.Case),
            Charging(battery.Left), Charging(battery.Right), Charging(battery.Case), source);
        return sample.HasLevel ? sample : null;
    }
}
