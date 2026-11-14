using System.Globalization;

namespace AppleDeviceHub.Core.Battery;

/// <summary>How much time the history chart shows.</summary>
public enum BatteryChartRange
{
    Day = 0,
    Week = 1,
}

/// <summary>
/// One unbroken stretch of a line, in plot coordinates (origin top-left, y grows downward).
/// A reading that stands alone, with nothing before or after it that can be trusted, is a dot.
/// </summary>
public sealed record ChartSegment(IReadOnlyList<(double X, double Y)> Points, bool IsDot);

/// <summary>A horizontal span (plot x) in which a charging reading was recorded.</summary>
public sealed record ChartBand(double X1, double X2);

public sealed record ChartGridline(int Percent, double Y);

/// <summary>A time label centred on <see cref="X"/>.</summary>
public sealed record ChartTick(double X, string Label);

/// <summary>Time labels, plus the x positions of the faint vertical lines that go with them.</summary>
public sealed record ChartAxis(IReadOnlyList<ChartTick> Labels, IReadOnlyList<double> Lines);

/// <summary>
/// What was known at one recorded reading. A part that is not known at that moment is null (shown as a dash),
/// never zero.
/// </summary>
public sealed record ChartProbe(DateTimeOffset Time, double X, int? Left, int? Right, int? Case, bool Charging);

/// <summary>
/// Geometry for the battery history chart, kept out of the UI so it can be tested. Battery readings are only
/// stored when something changes, so a level is held until the next reading, with two honest limits:
/// <list type="bullet">
/// <item>if the whole device was silent for longer than <see cref="MaxGap"/>, every line breaks there;</item>
/// <item>a part that stops being reported (for example the case while the pods are in the ears) is held for at
/// most <see cref="MaxSeriesAge"/>.</item>
/// </list>
/// Nothing is ever drawn down to zero for "unknown".
/// </summary>
public sealed class BatteryChartModel
{
    /// <summary>Silence between two recorded readings longer than this breaks the lines.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromHours(3);

    /// <summary>A part that is no longer reported keeps its last level for at most this long.</summary>
    public static readonly TimeSpan MaxSeriesAge = TimeSpan.FromHours(12);

    private BatteryChartModel(BatteryChartRange range, DateTimeOffset start, DateTimeOffset end, double width, double height,
        IReadOnlyList<ChartSegment> left, IReadOnlyList<ChartSegment> right, IReadOnlyList<ChartSegment> @case,
        IReadOnlyList<ChartBand> charging, IReadOnlyList<ChartProbe> probes, ChartAxis axis)
    {
        Range = range;
        Start = start;
        End = end;
        Width = width;
        Height = height;
        Left = left;
        Right = right;
        Case = @case;
        Charging = charging;
        Probes = probes;
        Axis = axis;
        Gridlines = new[] { 100, 50, 0 }.Select(p => new ChartGridline(p, YOf(p, height))).ToList();
    }

    public BatteryChartRange Range { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public double Width { get; }
    public double Height { get; }
    public IReadOnlyList<ChartSegment> Left { get; }
    public IReadOnlyList<ChartSegment> Right { get; }
    public IReadOnlyList<ChartSegment> Case { get; }
    public IReadOnlyList<ChartBand> Charging { get; }

    /// <summary>One entry per recorded reading inside the range, oldest first.</summary>
    public IReadOnlyList<ChartProbe> Probes { get; }

    public ChartAxis Axis { get; }

    /// <summary>The 100, 50 and 0 % lines.</summary>
    public IReadOnlyList<ChartGridline> Gridlines { get; }

    /// <summary>True when anything is drawn for the range.</summary>
    public bool HasData => Probes.Count > 0 || Left.Count + Right.Count + Case.Count > 0;

    /// <summary>The window length of <paramref name="range"/>.</summary>
    public static TimeSpan WindowOf(BatteryChartRange range) => range == BatteryChartRange.Week ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);

    private static double YOf(int percent, double height) => height * (1 - Math.Clamp(percent, 0, 100) / 100.0);

    /// <summary>
    /// The range to open with: the last 7 days when they hold more than half a week of history (or the last 24 hours
    /// are empty but older readings exist); otherwise the last 24 hours, where a short history is easier to read.
    /// </summary>
    public static BatteryChartRange ChooseRange(IReadOnlyList<BatterySample> samples, DateTimeOffset now)
    {
        var weekStart = now - WindowOf(BatteryChartRange.Week);
        var dayStart = now - WindowOf(BatteryChartRange.Day);
        DateTimeOffset? first = null;
        bool anyInDay = false;
        foreach (var s in samples)
        {
            if (s.Time > now || s.Time < weekStart) continue;
            if (first is null || s.Time < first) first = s.Time;
            if (s.Time >= dayStart) anyInDay = true;
        }
        if (first is null) return BatteryChartRange.Day;
        if (!anyInDay) return BatteryChartRange.Week;
        return now - first.Value >= TimeSpan.FromDays(3.5) ? BatteryChartRange.Week : BatteryChartRange.Day;
    }

    /// <summary>
    /// Builds the chart for <paramref name="range"/> ending at <paramref name="now"/>, drawn into a plot
    /// <paramref name="width"/> by <paramref name="height"/> (no margins; the view adds those).
    /// </summary>
    public static BatteryChartModel Build(IReadOnlyList<BatterySample> samples, DateTimeOffset now, BatteryChartRange range,
        double width, double height, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var start = now - WindowOf(range);
        double total = (now - start).TotalSeconds;
        double X(DateTimeOffset t) => Math.Clamp((t - start).TotalSeconds / total * width, 0, width);

        var ordered = samples.Where(s => s.Time <= now).OrderBy(s => s.Time).ToList();
        Func<BatterySample, int?>[] picks = { s => s.Left, s => s.Right, s => s.Case };
        var finished = new List<Run>[] { new(), new(), new() };
        var open = new Run?[3];
        var held = new int?[3];
        var probes = new List<ChartProbe>();
        var bands = new List<(DateTimeOffset From, DateTimeOffset To)>();
        DateTimeOffset? chargeFrom = null;
        DateTimeOffset? previous = null;

        void Close(int k, DateTimeOffset candidate)
        {
            if (open[k] is not { } run) return;
            run.End = EndFor(run, candidate);
            finished[k].Add(run);
            open[k] = null;
        }

        foreach (var s in ordered)
        {
            if (previous is { } p && s.Time - p > MaxGap)
            {
                for (int k = 0; k < 3; k++) Close(k, p);
                if (chargeFrom is { } cf) { bands.Add((cf, p)); chargeFrom = null; }
            }

            for (int k = 0; k < 3; k++)
            {
                if (open[k] is { } stale && s.Time - stale.LastReading > MaxSeriesAge)
                    Close(k, previous ?? s.Time);
                if (picks[k](s) is int raw)
                {
                    int level = Math.Clamp(raw, 0, 100);
                    if (open[k] is not { } run)
                    {
                        run = new Run();
                        open[k] = run;
                        run.Vertices.Add((s.Time, level));
                    }
                    else if (run.Vertices[^1].Level != level) run.Vertices.Add((s.Time, level));
                    run.LastReading = s.Time;
                }
                held[k] = open[k]?.Vertices[^1].Level;
            }

            bool charging = s.LeftCharging == true || s.RightCharging == true || s.CaseCharging == true;
            if (charging) chargeFrom ??= s.Time;
            else if (chargeFrom is { } from) { bands.Add((from, s.Time)); chargeFrom = null; }

            if (s.Time >= start) probes.Add(new ChartProbe(s.Time, X(s.Time), held[0], held[1], held[2], charging));
            previous = s.Time;
        }

        if (previous is { } last)
        {
            var tail = now - last <= MaxGap ? now : last;
            for (int k = 0; k < 3; k++) Close(k, tail);
            if (chargeFrom is { } cf) bands.Add((cf, tail));
        }

        var segments = new IReadOnlyList<ChartSegment>[3];
        for (int k = 0; k < 3; k++)
            segments[k] = finished[k].Select(r => ToSegment(r, start, now, X, height)).OfType<ChartSegment>().ToList();

        var chargingBands = new List<ChartBand>();
        foreach (var (from, to) in bands)
        {
            if (to < start) continue;
            double x1 = X(from < start ? start : from);
            double x2 = X(to);
            if (x2 - x1 < 2) x2 = Math.Min(width, x1 + 2);
            if (chargingBands.Count > 0 && x1 <= chargingBands[^1].X2 + 0.5)
                chargingBands[^1] = new ChartBand(chargingBands[^1].X1, Math.Max(x2, chargingBands[^1].X2));
            else chargingBands.Add(new ChartBand(x1, x2));
        }

        var axis = BuildAxis(range, start, now, width, X, zone, culture);
        return new BatteryChartModel(range, start, now, width, height, segments[0], segments[1], segments[2], chargingBands, probes, axis);
    }

    private sealed class Run
    {
        public List<(DateTimeOffset Time, int Level)> Vertices { get; } = new();
        public DateTimeOffset LastReading { get; set; }
        public DateTimeOffset End { get; set; }
    }

    private static DateTimeOffset EndFor(Run run, DateTimeOffset candidate)
    {
        var limit = run.LastReading + MaxSeriesAge;
        var end = candidate < limit ? candidate : limit;
        var lastVertex = run.Vertices[^1].Time;
        return end < lastVertex ? lastVertex : end;
    }

    private static ChartSegment? ToSegment(Run run, DateTimeOffset start, DateTimeOffset now, Func<DateTimeOffset, double> x, double height)
    {
        if (run.End < start) return null;
        var vertices = new List<(DateTimeOffset Time, int Level)>();
        foreach (var v in run.Vertices)
        {
            if (v.Time > now) break;
            if (v.Time <= start) { vertices.Clear(); vertices.Add((start, v.Level)); }
            else vertices.Add(v);
        }
        if (vertices.Count == 0) return null;

        var points = new List<(double X, double Y)>();
        for (int i = 0; i < vertices.Count; i++)
        {
            double vx = x(vertices[i].Time);
            if (i > 0) points.Add((vx, YOf(vertices[i - 1].Level, height)));
            points.Add((vx, YOf(vertices[i].Level, height)));
        }
        var endTime = run.End > now ? now : run.End;
        double endX = x(endTime);
        points.Add((endX, YOf(vertices[^1].Level, height)));
        bool dot = vertices.Count == 1 && endX - points[0].X < 1;
        if (dot) points = new List<(double X, double Y)> { points[0] };
        return new ChartSegment(points, dot);
    }

    /// <summary>The index of the recorded reading nearest to plot position <paramref name="x"/>, or -1 when there are none.</summary>
    public int NearestProbe(double x)
    {
        if (Probes.Count == 0) return -1;
        int lo = 0, hi = Probes.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Probes[mid].X < x) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(Probes[lo - 1].X - x) <= Math.Abs(Probes[lo].X - x)) lo--;
        return lo;
    }

    /// <summary>The plain sentence for one reading, e.g. "Today 3:40 PM · L 80% · R 80% · Case 60%".</summary>
    public static string Describe(ChartProbe probe, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(probe.Time, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        int days = (today - local.Date).Days;
        string day = days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 6 => local.ToString("ddd", culture),
            _ => local.ToString("MMM d", culture),
        };
        string time = local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        string Part(string name, int? level) => string.Create(culture, $"{name} {(level is int l ? l + "%" : "—")}");
        string text = $"{day} {time} · {Part("L", probe.Left)} · {Part("R", probe.Right)} · {Part("Case", probe.Case)}";
        return probe.Charging ? text + " · Charging" : text;
    }

    private static ChartAxis BuildAxis(BatteryChartRange range, DateTimeOffset start, DateTimeOffset end, double width,
        Func<DateTimeOffset, double> x, TimeZoneInfo zone, CultureInfo culture)
    {
        var labels = new List<ChartTick>();
        var lines = new List<double>();
        var localStart = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(end, zone).DateTime;

        DateTimeOffset At(DateTime local) => new(local, zone.GetUtcOffset(local));

        if (range == BatteryChartRange.Day)
        {
            int step = width >= 220 ? 6 : 12;
            bool twentyFour = culture.DateTimeFormat.ShortTimePattern.Contains('H');
            for (var day = localStart.Date; day <= localEnd.Date; day = day.AddDays(1))
            {
                for (int hour = 0; hour < 24; hour += step)
                {
                    var t = At(day.AddHours(hour));
                    if (t < start || t > end) continue;
                    double tx = x(t);
                    labels.Add(new ChartTick(tx, HourLabel(hour, twentyFour, culture)));
                    lines.Add(tx);
                }
            }
        }
        else
        {
            for (var day = localStart.Date; day <= localEnd.Date; day = day.AddDays(1))
            {
                var from = At(day);
                var to = At(day.AddDays(1));
                var shownFrom = from < start ? start : from;
                var shownTo = to > end ? end : to;
                if (from > start && from < end) lines.Add(x(from));
                double visible = (shownTo - shownFrom).TotalHours;
                if (visible < 6) continue;   // a sliver of a day is too narrow to name
                labels.Add(new ChartTick((x(shownFrom) + x(shownTo)) / 2, day.ToString("ddd", culture)));
            }
        }
        return new ChartAxis(labels, lines);
    }

    private static string HourLabel(int hour, bool twentyFour, CultureInfo culture)
    {
        if (twentyFour) return string.Create(CultureInfo.InvariantCulture, $"{hour:00}:00");
        int h12 = hour % 12 == 0 ? 12 : hour % 12;
        string designator = hour < 12 ? culture.DateTimeFormat.AMDesignator : culture.DateTimeFormat.PMDesignator;
        return string.Create(CultureInfo.InvariantCulture, $"{h12} {designator}");
    }
}

/// <summary>The one plain line under the chart.</summary>
public static class BatteryChartSummary
{
    public const string ChargingText = "Charging";
    public const string NotEnoughText = "Not enough history yet";
    public const string StaleText = "No recent reading";

    public static string Text(IReadOnlyList<BatterySample> samples, DateTimeOffset now, DateTimeOffset? lastHeardAt = null)
    {
        var estimate = DrainEstimator.Estimate(samples, now, lastHeardAt);
        if (estimate is { Kind: EstimateKind.Estimate, Low: { } low, High: { } high })
        {
            double hours = (low.TotalHours + high.TotalHours) / 2;
            if (hours < 1.5)
            {
                int minutes = (int)Math.Max(5, Math.Round(hours * 12) * 5);
                return string.Create(CultureInfo.InvariantCulture, $"About {minutes} min left at this rate");
            }
            double rounded = Math.Round(hours * 2, MidpointRounding.AwayFromZero) / 2;
            return string.Create(CultureInfo.InvariantCulture, $"About {rounded:0.#} hr left at this rate");
        }
        if (estimate.Text == DrainEstimator.ChargingText) return ChargingText;
        if (estimate.Text == DrainEstimator.StaleText)
        {
            // An old charging reading must not read as "Charging" now; say when it was last seen that way.
            var latest = samples.LastOrDefault(s => s.LowestPod is not null || s.PodCharging);
            if (latest is { PodCharging: true })
                return "Last seen charging, " + LastSeenTracker.Ago(now - latest.Time);
            return StaleText;
        }
        return NotEnoughText;
    }
}
