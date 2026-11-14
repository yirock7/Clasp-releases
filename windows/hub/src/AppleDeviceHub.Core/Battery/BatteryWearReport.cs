using System.Globalization;

namespace AppleDeviceHub.Core.Battery;

public enum WearReportKind
{
    NotEnoughData = 0,
    Comparable = 1,
}

/// <summary>Result of comparing early and recent drain. Rates are percent of battery per hour.</summary>
public sealed record BatteryWearReport(WearReportKind Kind, double? EarlyRatePerHour, double? RecentRatePerHour, int? ChangePercent, string Text)
{
    public const string NotEnoughDataText = "Not enough history yet to compare battery drain over time";

    /// <summary>Difference below which the two periods are called about the same.</summary>
    public const double SameBand = 0.15;

    public static BatteryWearReport Build(IReadOnlyList<BatterySample> samples, DateTimeOffset now, TimeSpan? period = null, int minSegments = 3)
    {
        var window = period ?? TimeSpan.FromDays(14);
        var none = new BatteryWearReport(WearReportKind.NotEnoughData, null, null, null, NotEnoughDataText);
        if (samples.Count == 0) return none;
        var first = samples[0].Time;
        // The two periods must not overlap, so the history has to span both of them.
        if (now - first < window + window) return none;

        var segments = DischargeAnalysis.Segments(samples);
        var early = segments.Where(s => s.Start < first + window).Select(s => s.RatePerHour).ToList();
        var recent = segments.Where(s => s.Start >= now - window).Select(s => s.RatePerHour).ToList();
        if (early.Count < minSegments || recent.Count < minSegments) return none;

        double earlyRate = Median(early);
        double recentRate = Median(recent);
        if (earlyRate <= 0) return none;
        double change = recentRate / earlyRate - 1;
        int rounded = (int)(Math.Round(change * 100 / 5, MidpointRounding.AwayFromZero) * 5);
        return new BatteryWearReport(WearReportKind.Comparable, earlyRate, recentRate, rounded, Describe(change, rounded));
    }

    private static string Describe(double change, int rounded)
    {
        const string caveat = " Drain also depends on volume, calls and how you use them, so this is a hint, not a diagnosis.";
        if (Math.Abs(change) < SameBand)
            return "Battery drain looks about the same as in your first two weeks of recording." + caveat;
        string amount = Math.Abs(rounded).ToString(CultureInfo.InvariantCulture);
        return change > 0
            ? $"Your AirPods now drain about {amount}% faster than in your first two weeks of recording, which can be a sign of battery wear." + caveat
            : $"Your AirPods now drain about {amount}% slower than in your first two weeks of recording, probably because of how you use them." + caveat;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
