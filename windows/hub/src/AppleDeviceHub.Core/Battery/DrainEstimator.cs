using System.Globalization;

namespace AppleDeviceHub.Core.Battery;

/// <summary>
/// One uninterrupted stretch of discharge of the lowest earbud. <see cref="Start"/> is the time
/// the level first dropped to the second recorded level (a real step edge; the first recorded
/// level may have been joined mid-step), <see cref="End"/> the last drop.
/// </summary>
internal sealed record DischargeSegment(DateTimeOffset Start, DateTimeOffset End, int DropPercent, int StepDrops, double RatePerHour);

internal static class DischargeAnalysis
{
    /// <summary>A longer silence between two level changes means the pods were out of earshot or in use unobserved.</summary>
    public static readonly TimeSpan MaxStepGap = TimeSpan.FromHours(3);

    /// <summary>
    /// Splits history into discharge segments with at least two observed step drops (so at least
    /// one fully timed step). Charging, a level rise or a long gap ends a segment.
    /// </summary>
    public static IReadOnlyList<DischargeSegment> Segments(IReadOnlyList<BatterySample> samples)
    {
        var result = new List<DischargeSegment>();
        var points = new List<(DateTimeOffset Time, int Level)>();

        void Close()
        {
            if (points.Count >= 3)
            {
                var first = points[1];
                var last = points[^1];
                double hours = (last.Time - first.Time).TotalHours;
                int drop = first.Level - last.Level;
                if (hours > 0 && drop > 0)
                    result.Add(new DischargeSegment(first.Time, last.Time, drop, points.Count - 1, drop / hours));
            }
            points.Clear();
        }

        foreach (var s in samples)
        {
            if (s.PodCharging) { Close(); continue; }
            if (s.LowestPod is not int level) continue;
            if (points.Count == 0) { points.Add((s.Time, level)); continue; }
            var (lastTime, lastLevel) = points[^1];
            if (level == lastLevel) continue;
            if (level > lastLevel || s.Time - lastTime > MaxStepGap)
            {
                Close();
                points.Add((s.Time, level));
                continue;
            }
            points.Add((s.Time, level));
        }
        Close();
        return result;
    }
}

public enum EstimateKind
{
    NotEnoughData = 0,
    Estimate = 1,
}

/// <summary>A time-remaining estimate as a range, or an honest "not enough data".</summary>
public sealed record DrainEstimate(EstimateKind Kind, TimeSpan? Low, TimeSpan? High, string Text)
{
    public static DrainEstimate NotEnough(string text) => new(EstimateKind.NotEnoughData, null, null, text);
}

public sealed record DrainEstimatorOptions
{
    /// <summary>Only discharge from this far back is used.</summary>
    public TimeSpan Lookback { get; init; } = TimeSpan.FromDays(7);

    /// <summary>At most this many of the most recent usable segments are combined.</summary>
    public int MaxSegments { get; init; } = 3;

    /// <summary>The latest reading must be this fresh (against the last time the pods were heard, when known).</summary>
    public TimeSpan MaxReadingAge { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The rate is widened by this fraction each way. Levels also have a 10 % step of uncertainty.</summary>
    public double RateSpread { get; init; } = 0.25;
}

/// <summary>
/// Estimates time left from recent discharge of the lowest earbud. Never a single number: the
/// answer is a range built from the observed rate widened for noise and for the 10 % battery
/// steps, or "not enough data" (needs at least two observed step drops in one uninterrupted
/// stretch, not charging, with a fresh reading). Pure; the clock is passed in.
/// </summary>
public static class DrainEstimator
{
    public const string NotEnoughDataText = "Not enough data yet for a time estimate";
    public const string ChargingText = "Charging, so no time estimate";
    public const string StaleText = "No recent reading, so no time estimate";

    public static DrainEstimate Estimate(IReadOnlyList<BatterySample> samples, DateTimeOffset now, DateTimeOffset? lastHeardAt = null, DrainEstimatorOptions? options = null)
    {
        options ??= new DrainEstimatorOptions();
        var latest = samples.LastOrDefault(s => s.LowestPod is not null || s.PodCharging);
        if (latest is null || latest.LowestPod is not int level) return DrainEstimate.NotEnough(NotEnoughDataText);
        // Freshness first: a charging reading from two days ago says nothing about now.
        var reference = lastHeardAt ?? latest.Time;
        if (now - reference > options.MaxReadingAge) return DrainEstimate.NotEnough(StaleText);
        if (latest.PodCharging) return DrainEstimate.NotEnough(ChargingText);

        var recent = DischargeAnalysis.Segments(samples)
            .Where(s => s.End >= now - options.Lookback)
            .OrderBy(s => s.End)
            .TakeLast(Math.Max(1, options.MaxSegments))
            .ToList();
        if (recent.Count == 0) return DrainEstimate.NotEnough(NotEnoughDataText);

        double hours = recent.Sum(s => (s.End - s.Start).TotalHours);
        double drop = recent.Sum(s => s.DropPercent);
        if (hours <= 0 || drop <= 0) return DrainEstimate.NotEnough(NotEnoughDataText);
        double rate = drop / hours;

        double lowHours = level / (rate * (1 + options.RateSpread));
        double highHours = Math.Min(100, level + 10) / (rate * (1 - options.RateSpread));
        // Round outwards to 5 minutes so rounding never makes the range look tighter.
        var low = TimeSpan.FromMinutes(Math.Floor(lowHours * 12) * 5);
        var high = TimeSpan.FromMinutes(Math.Ceiling(highHours * 12) * 5);
        if (low > TimeSpan.FromHours(24)) return DrainEstimate.NotEnough(NotEnoughDataText);
        if (high > TimeSpan.FromHours(24)) high = TimeSpan.FromHours(24);
        if (high <= low) high = low + TimeSpan.FromMinutes(5);
        return new DrainEstimate(EstimateKind.Estimate, low, high, Format(low, high));
    }

    /// <summary>Plain wording, always labelled as an estimate.</summary>
    public static string Format(TimeSpan low, TimeSpan high)
    {
        if (high <= TimeSpan.FromMinutes(90))
            return string.Create(CultureInfo.InvariantCulture, $"Estimate: about {low.TotalMinutes:0} to {high.TotalMinutes:0} minutes left");
        double lowH = Math.Floor(low.TotalHours * 2) / 2;
        double highH = Math.Ceiling(high.TotalHours * 2) / 2;
        return string.Create(CultureInfo.InvariantCulture, $"Estimate: about {lowH:0.#} to {highH:0.#} hours left");
    }
}
