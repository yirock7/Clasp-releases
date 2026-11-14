namespace AppleDeviceHub.Core.Ble;

/// <summary>
/// What one heard broadcast says about its pair, reduced to the facts used to decide whether two
/// addresses are the two buds of the same pair. Null means the message did not report it.
/// </summary>
public readonly record struct PodReading(ushort Model, int? Left, int? Right, int? Case, byte? LidCounter, bool? InCase = null)
{
    public static PodReading From(ProximityMessage message)
        => new(message.ModelId ?? 0, message.LeftBatteryPercent, message.RightBatteryPercent, message.CaseBatteryPercent, message.LidCounter, message.ThisPodInCase);
}

/// <summary>
/// Each earbud of a pair broadcasts from its own address, so one pair is heard as two sources.
/// Two readings are "siblings" (the same pair) when they share the model, agree on the lid counter
/// and each of left, right and case is within the battery tolerance. A value that either side did
/// not report is not compared, but at least one value must be compared: nothing else is claimed.
/// Left and right may be matched straight or swapped (see <see cref="AreSiblings"/>).
/// The lid counter is compared only when both readings carry a case level and neither comes from a bud
/// known to be OUT of the case. A bud that left the case keeps the counter it last saw, so when the pair
/// is split (one bud in an ear or on the desk, the other in the case or put back and taken out again) the
/// two buds legitimately disagree on it; an in-ear message (case level unknown) is likewise not comparable.
/// Batteries stay the evidence in that state.
/// Two different pairs of the same model in the same state and with similar batteries cannot be told
/// apart and also group together; clearly different batteries or lid counters keep them separate.
/// </summary>
public static class BroadcastSiblings
{
    public static bool AreSiblings(PodReading a, PodReading b, int batteryTolerancePercent)
    {
        if (a.Model != b.Model) return false;
        if (LidConflict(a, b)) return false;

        // Which side is "left" depends on the primary-bud flip, which is an upstream assumption seen only
        // with equal levels. The two buds report opposite primary bits, so the sides may be swapped between
        // them: either the straight or the swapped left/right comparison may match.
        var caseCompared = Compare(a.Case, b.Case, batteryTolerancePercent);
        if (caseCompared is null) return false;
        var straight = Compare(a.Left, b.Left, batteryTolerancePercent) is { } l1 && Compare(a.Right, b.Right, batteryTolerancePercent) is { } r1
            ? l1 + r1 : (int?)null;
        var swapped = Compare(a.Left, b.Right, batteryTolerancePercent) is { } l2 && Compare(a.Right, b.Left, batteryTolerancePercent) is { } r2
            ? l2 + r2 : (int?)null;
        // Of the orientations that agree, use the one that compares the most values: an orientation that
        // agrees only because a side was unknown (0 compared) must not hide one that really matched.
        int? pods = (straight, swapped) switch
        {
            (int s, int w) => Math.Max(s, w),
            (int s, null) => s,
            (null, int w) => w,
            _ => null,
        };
        if (pods is null) return false;
        return pods.Value + caseCompared.Value > 0;
    }

    /// <summary>
    /// Why two readings are NOT siblings, in plain words with the actual values (battery levels, lid counter,
    /// model id; never an address), or null when they are siblings. Diagnostic only: it repeats the checks of
    /// <see cref="AreSiblings"/> in the same order and never changes what groups together.
    /// </summary>
    public static string? Disagreement(PodReading a, PodReading b, int batteryTolerancePercent)
    {
        if (a.Model != b.Model) return $"model 0x{a.Model:X4} vs 0x{b.Model:X4}";
        if (LidConflict(a, b)) return $"lid counter {a.LidCounter} vs {b.LidCounter}";
        if (Compare(a.Case, b.Case, batteryTolerancePercent) is null) return $"case {Level(a.Case)} vs {Level(b.Case)}";
        if (AreSiblings(a, b, batteryTolerancePercent)) return null;
        if (a.Left is null && a.Right is null && a.Case is null || b.Left is null && b.Right is null && b.Case is null)
            return "no battery level to compare";
        if (Compare(a.Left, b.Left, batteryTolerancePercent) is null || Compare(a.Right, b.Right, batteryTolerancePercent) is null)
            return $"left/right {Level(a.Left)}/{Level(a.Right)} vs {Level(b.Left)}/{Level(b.Right)}";
        return "nothing comparable (no battery level reported by both)";
    }

    /// <summary>
    /// Two lid counters that differ prove two pairs only when both buds were in the case (a bud out of the
    /// case holds a stale counter) and both readings carry a case level.
    /// </summary>
    private static bool LidConflict(PodReading a, PodReading b)
        => a.Case is not null && b.Case is not null
           && a.LidCounter is byte lidA && b.LidCounter is byte lidB && lidA != lidB
           && a.InCase != false && b.InCase != false;

    private static string Level(int? value) => value is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : "?";

    /// <summary>Number of values compared (0 or 1) when they agree, null when both are known and differ too much.</summary>
    private static int? Compare(int? x, int? y, int tolerance)
    {
        if (x is not int px || y is not int py) return 0;
        return Math.Abs(px - py) <= tolerance ? 1 : null;
    }

    /// <summary>How many distinct pairs the readings describe (siblings count once).</summary>
    public static int CountGroups(IEnumerable<PodReading> readings, int batteryTolerancePercent)
        => GroupSiblings(readings, r => r, batteryTolerancePercent).Count;

    /// <summary>
    /// Groups items by pair, in the order given. An item joins a group only if it is a sibling of EVERY
    /// member: a partial reading (an unknown value is not compared) must not bridge two readings that
    /// conflict with each other, because sibling-ness is not transitive.
    /// </summary>
    public static List<List<T>> GroupSiblings<T>(IEnumerable<T> items, Func<T, PodReading> readingOf, int batteryTolerancePercent)
    {
        var groups = new List<List<T>>();
        foreach (var item in items)
        {
            var reading = readingOf(item);
            var home = groups.Find(group => group.TrueForAll(member => AreSiblings(readingOf(member), reading, batteryTolerancePercent)));
            if (home is null) groups.Add([item]);
            else home.Add(item);
        }
        return groups;
    }
}
