using System.Globalization;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Battery;

public enum WearReportKind
{
    NotEnoughData = 0,
    Comparable = 1,
}

/// <summary>Result of comparing early and recent drain. Rates are percent of battery per hour.</summary>
public sealed record BatteryWearReport(WearReportKind Kind, double? EarlyRatePerHour, double? RecentRatePerHour, int? ChangePercent, string Text)
{
    public static string NotEnoughDataText => Strings.Battery_Wear_NotEnoughData;

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
        if (Math.Abs(change) < SameBand)
            return Strings.Battery_Wear_AboutTheSame;
        string amount = Math.Abs(rounded).ToString(CultureInfo.InvariantCulture);
        return change > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Battery_Wear_Faster, amount)
            : string.Format(CultureInfo.CurrentCulture, Strings.Battery_Wear_Slower, amount);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
