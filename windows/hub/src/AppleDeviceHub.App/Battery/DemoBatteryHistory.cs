using AppleDeviceHub.Core.Battery;

namespace AppleDeviceHub.App.Battery;

/// <summary>
/// Made-up readings for screenshots only (<c>--demo-history</c>, demo transport only). They live in memory and are
/// never written to the real history file.
/// </summary>
internal static class DemoBatteryHistory
{
    /// <summary>Set by App when the demo transport starts with <c>--demo-history</c>.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Set with <c>--demo-history=sparse</c>: the irregular-use pattern instead of four steady days.</summary>
    public static bool Sparse { get; set; }

    /// <summary>
    /// Irregular use over a week, for checking the compressed chart: a short session about six days ago with a
    /// charging stop, one lone reading five days ago, a session two evenings ago, a session last night, and one
    /// charging reading 12 hours ago with nothing around it.
    /// </summary>
    public static IReadOnlyList<BatterySample> GenerateSparse(DateTimeOffset now)
    {
        var samples = new List<BatterySample>();
        void Add(DateTimeOffset t, int? l, int? r, int? c, bool charging = false)
        {
            if (t > now) return;
            samples.Add(new BatterySample(t, l, r, c, charging ? true : null, charging ? true : null, charging ? true : null, BatterySampleSource.Driver));
        }

        var a = now.AddDays(-6);
        Add(a, 90, 90, 80);
        Add(a.AddMinutes(12), 82, 82, null);
        Add(a.AddMinutes(25), 70, 72, null, charging: true);
        Add(a.AddMinutes(40), 80, 80, null, charging: true);
        Add(now.AddDays(-5), 55, 55, 70);
        var b = now.AddDays(-2);
        Add(b, 100, 100, 70);
        Add(b.AddMinutes(20), 90, 90, null);
        Add(b.AddMinutes(45), 80, 80, null);
        var c0 = now.AddHours(-26);
        Add(c0, 75, 75, 60);
        Add(c0.AddMinutes(30), 60, 60, null);
        Add(c0.AddMinutes(60), 50, 50, null);
        Add(now.AddHours(-12), 45, 45, 55, charging: true);
        samples.Sort((x, y) => x.Time.CompareTo(y.Time));
        return samples;
    }

    /// <summary>
    /// Four days of use: a morning and an afternoon of listening in 10 % steps, a charging stop at midday, and a
    /// quiet night. One afternoon has no readings at all, so the chart shows a real gap.
    /// </summary>
    public static IReadOnlyList<BatterySample> Generate(DateTimeOffset now)
    {
        var samples = new List<BatterySample>();
        var day0 = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        void Add(DateTimeOffset t, int? l, int? r, int? c, bool charging = false)
        {
            if (t > now) return;
            samples.Add(new BatterySample(t, l, r, c, charging ? true : null, charging ? true : null, charging ? true : null, BatterySampleSource.Driver));
        }

        for (int back = 3; back >= 0; back--)
        {
            var day = day0.AddDays(-back);
            int caseLevel = 100 - (3 - back) * 10;
            // Morning commute: 100 down to 60, the right earbud a step off now and then.
            for (int i = 0; i <= 4; i++)
            {
                int l = 100 - i * 10;
                int r = i == 2 ? 80 : l;
                Add(day.AddHours(7.5).AddMinutes(i * 46), l, r, i == 0 ? caseLevel : null);
            }
            if (back == 2) continue;   // no readings for this afternoon: a real gap
            // Midday charging: back in the case.
            Add(day.AddHours(11.5), 60, 60, caseLevel - 10, charging: true);
            Add(day.AddHours(12.1), 80, 80, caseLevel - 10, charging: true);
            Add(day.AddHours(12.6), 100, 100, caseLevel - 10, charging: true);
            Add(day.AddHours(13), 100, 100, caseLevel - 10);
            // Afternoon and evening.
            for (int i = 1; i <= 7; i++)
                Add(day.AddHours(14.5).AddMinutes(i * 52), 100 - i * 10, 100 - i * 10 + (i % 3 == 0 ? 10 : 0), i == 1 ? caseLevel - 10 : null);
        }
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        return samples;
    }
}
