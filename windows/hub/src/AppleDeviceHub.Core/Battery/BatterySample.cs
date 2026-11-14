namespace AppleDeviceHub.Core.Battery;

/// <summary>Where a battery reading came from.</summary>
public enum BatterySampleSource
{
    /// <summary>A nearby Bluetooth broadcast (10 % steps; only recorded at identity Probable or better).</summary>
    Broadcast = 0,

    /// <summary>The Clasp driver / engine session.</summary>
    Driver = 1,

    /// <summary>The percentage Windows reports for the paired device.</summary>
    Windows = 2,
}

/// <summary>
/// One timestamped battery reading. A null level means "not reported", never zero. Levels are
/// whatever the source reported (broadcast: multiples of 10).
/// </summary>
public sealed record BatterySample(
    DateTimeOffset Time,
    int? Left,
    int? Right,
    int? Case,
    bool? LeftCharging,
    bool? RightCharging,
    bool? CaseCharging,
    BatterySampleSource Source)
{
    /// <summary>True when at least one level is reported.</summary>
    public bool HasLevel => Left is not null || Right is not null || Case is not null;

    /// <summary>The lowest reported earbud level, or null when neither earbud is reported.</summary>
    public int? LowestPod => (Left, Right) switch
    {
        (int l, int r) => Math.Min(l, r),
        (int l, null) => l,
        (null, int r) => r,
        _ => null,
    };

    /// <summary>True when a reported earbud is charging.</summary>
    public bool PodCharging => LeftCharging == true || RightCharging == true;

    /// <summary>True when the reading is the same as <paramref name="other"/> apart from time and source.</summary>
    public bool SameValues(BatterySample other) =>
        Left == other.Left && Right == other.Right && Case == other.Case
        && LeftCharging == other.LeftCharging && RightCharging == other.RightCharging && CaseCharging == other.CaseCharging;
}

/// <summary>Where recorded data lives. Core never touches files; the app supplies the implementation.</summary>
public interface IBatteryHistoryStore
{
    /// <summary>The stored text for <paramref name="name"/>, or null when absent or unreadable.</summary>
    string? Read(string name);

    /// <summary>Replaces the stored text; returns false when it could not be written.</summary>
    bool Write(string name, string content);
}

/// <summary>An in-memory store (tests, and the fallback when no file store is wired).</summary>
public sealed class InMemoryBatteryHistoryStore : IBatteryHistoryStore
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

    public string? Read(string name) => _items.TryGetValue(name, out var text) ? text : null;

    public bool Write(string name, string content)
    {
        _items[name] = content;
        return true;
    }
}
