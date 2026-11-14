using System.Globalization;
using AppleDeviceHub.Core.Battery;

namespace AppleDeviceHub.Core.Tests.Battery;

public class BatteryChartModelTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // 2026-10-02 12:00 UTC is a Friday.
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static BatteryChartModel Build(IReadOnlyList<BatterySample> samples, BatteryChartRange range = BatteryChartRange.Day, double width = 240, double height = 100, bool compress = false)
        => BatteryChartModel.Build(samples, Now, range, width, height, Utc, Inv, compress);

    private static BatterySample Reading(DateTimeOffset t, int? l, int? r, int? c, bool charging = false)
        => new(t, l, r, c, charging ? true : null, charging ? true : null, null, BatterySampleSource.Driver);

    private static (double, double) Round((double X, double Y) p) => (Math.Round(p.X, 3), Math.Round(p.Y, 3));

    // ---- mapping readings to points -------------------------------------------------------

    [Fact]
    public void Maps_readings_to_a_step_line_that_holds_until_now()
    {
        var samples = new[] { Reading(Now.AddHours(-3), 100, 100, 60), Reading(Now.AddHours(-2), 50, 50, 60) };
        var m = Build(samples);
        var line = Assert.Single(m.Left);
        Assert.Equal(new[] { (210.0, 0.0), (220.0, 0.0), (220.0, 50.0), (240.0, 50.0) }, line.Points.Select(Round));
        Assert.False(line.IsDot);
        Assert.Equal(40.0, Assert.Single(m.Case).Points[0].Y, 6);
    }

    [Fact]
    public void Gridlines_sit_at_100_50_and_0_percent()
    {
        var m = Build(Array.Empty<BatterySample>(), height: 80);
        Assert.Equal(new[] { (100, 0.0), (50, 40.0), (0, 80.0) }, m.Gridlines.Select(g => (g.Percent, g.Y)));
    }

    [Fact]
    public void A_reading_from_before_the_window_is_carried_to_the_left_edge()
    {
        var m = Build(new[] { Reading(Now.AddHours(-25), 80, 80, null), Reading(Now.AddHours(-23), 80, 80, null) });
        var line = Assert.Single(m.Left);
        Assert.Equal(0.0, line.Points[0].X);
        Assert.Equal(20.0, line.Points[0].Y, 6);
    }

    [Fact]
    public void Readings_after_now_are_ignored_and_no_samples_means_no_data()
    {
        Assert.False(Build(Array.Empty<BatterySample>()).HasData);
        Assert.False(Build(new[] { Reading(Now.AddHours(1), 50, 50, 50) }).HasData);
    }

    // ---- gaps ------------------------------------------------------------------------------

    [Fact]
    public void A_long_silence_breaks_the_line_instead_of_joining_across_it()
    {
        var samples = new[]
        {
            Reading(Now.AddHours(-20), 90, 90, null),
            Reading(Now.AddHours(-19), 80, 80, null),
            Reading(Now.AddHours(-6), 60, 60, null),     // 13 h of silence before this one
        };
        var m = Build(samples);
        Assert.Equal(2, m.Left.Count);
        Assert.True(m.Left[0].Points[^1].X < m.Left[1].Points[0].X);
        Assert.Equal(50.0, m.Left[0].Points[^1].X, 3);        // the first piece stops at the last reading it knew (-19 h)
        Assert.All(m.Left.SelectMany(s => s.Points), p => Assert.InRange(p.Y, 0, 100));
    }

    [Fact]
    public void A_stale_last_reading_does_not_run_on_to_now()
    {
        var m = Build(new[] { Reading(Now.AddHours(-10), 70, 70, null) });
        var only = Assert.Single(m.Left);
        Assert.True(only.IsDot);
        Assert.Single(only.Points);
    }

    [Fact]
    public void A_missing_part_never_draws_a_drop_to_zero()
    {
        var samples = new[] { Reading(Now.AddHours(-2), 100, 100, 70), Reading(Now.AddHours(-1), 90, 90, null) };
        var m = Build(samples);
        var c = Assert.Single(m.Case);
        Assert.All(c.Points, p => Assert.Equal(30.0, p.Y, 6));
    }

    [Fact]
    public void A_part_that_is_no_longer_reported_is_held_only_for_twelve_hours()
    {
        var samples = new List<BatterySample> { Reading(Now.AddHours(-23), 100, 100, 70) };
        for (int h = 21, level = 99; h >= 9; h -= 2, level--) samples.Add(Reading(Now.AddHours(-h), level, level, null));
        var m = Build(samples);
        var c = Assert.Single(m.Case);
        Assert.Equal(130.0, c.Points[^1].X, 3);               // held to -11 h (12 h after the reading), then it stops
        Assert.Equal(70, m.Probes[0].Case);
        Assert.Null(m.Probes[^1].Case);                       // the tooltip shows a dash from there on
    }

    // ---- charging --------------------------------------------------------------------------

    [Fact]
    public void Charging_readings_become_a_band_until_the_next_reading()
    {
        var samples = new[]
        {
            Reading(Now.AddHours(-12), 40, 40, null),
            Reading(Now.AddHours(-10), 40, 40, null, charging: true),
            Reading(Now.AddHours(-8), 70, 70, null),
        };
        var m = Build(samples);
        var band = Assert.Single(m.Charging);
        Assert.Equal(140.0, band.X1, 3);
        Assert.Equal(160.0, band.X2, 3);
        Assert.True(m.Probes[1].Charging);
        Assert.False(m.Probes[2].Charging);
    }

    // ---- nearest reading and the tooltip text ---------------------------------------------

    [Fact]
    public void Finds_the_nearest_reading_to_a_pointer_position()
    {
        var samples = new[] { Reading(Now.AddHours(-12), 100, 100, null), Reading(Now.AddHours(-9), 90, 90, null), Reading(Now.AddHours(-3), 80, 80, null) };
        var m = Build(samples);          // x = 120, 150, 210
        Assert.Equal(0, m.NearestProbe(-50));
        Assert.Equal(0, m.NearestProbe(130));
        Assert.Equal(1, m.NearestProbe(140));
        Assert.Equal(1, m.NearestProbe(179));
        Assert.Equal(2, m.NearestProbe(181));
        Assert.Equal(2, m.NearestProbe(500));
    }

    [Fact]
    public void Nearest_reading_is_minus_one_when_there_are_none()
        => Assert.Equal(-1, Build(Array.Empty<BatterySample>()).NearestProbe(10));

    [Fact]
    public void Describes_a_reading_in_plain_words()
    {
        var m = Build(new[] { Reading(Now.AddHours(-1).AddMinutes(-20), 80, 80, 60) });
        Assert.Equal("Today 10:40 · L 80% · R 80% · Case 60%", BatteryChartModel.Describe(m.Probes[0], Now, Utc, Inv));
    }

    [Fact]
    public void Unknown_parts_show_a_dash_and_charging_and_old_days_are_named()
    {
        Assert.Equal("Yesterday 11:00 · L 80% · R — · Case — · Charging",
            BatteryChartModel.Describe(new ChartProbe(Now.AddDays(-1).AddHours(-1), 0, 80, null, null, true), Now, Utc, Inv));
        Assert.Equal("Wed 12:00 · L 80% · R — · Case —", BatteryChartModel.Describe(new ChartProbe(Now.AddDays(-2), 0, 80, null, null, false), Now, Utc, Inv));
        Assert.Equal("Sep 26 12:00 · L 1% · R — · Case —", BatteryChartModel.Describe(new ChartProbe(Now.AddDays(-6), 0, 1, null, null, false), Now, Utc, Inv));
    }

    // ---- default range ---------------------------------------------------------------------

    [Fact]
    public void Opens_on_24_hours_when_the_week_is_mostly_empty()
    {
        var samples = new[] { Reading(Now.AddHours(-5), 80, 80, null), Reading(Now.AddHours(-1), 70, 70, null) };
        Assert.Equal(BatteryChartRange.Day, BatteryChartModel.ChooseRange(samples, Now));
        Assert.Equal(BatteryChartRange.Day, BatteryChartModel.ChooseRange(Array.Empty<BatterySample>(), Now));
    }

    [Fact]
    public void Opens_on_7_days_when_there_is_more_than_half_a_week_of_history()
    {
        var samples = new[] { Reading(Now.AddDays(-5), 80, 80, null), Reading(Now.AddHours(-1), 70, 70, null) };
        Assert.Equal(BatteryChartRange.Week, BatteryChartModel.ChooseRange(samples, Now));
    }

    [Fact]
    public void Opens_on_7_days_when_the_last_day_is_empty_but_older_readings_exist()
        => Assert.Equal(BatteryChartRange.Week, BatteryChartModel.ChooseRange(new[] { Reading(Now.AddDays(-2), 80, 80, null) }, Now));

    [Fact]
    public void Readings_older_than_a_week_do_not_count_for_the_default()
        => Assert.Equal(BatteryChartRange.Day, BatteryChartModel.ChooseRange(new[] { Reading(Now.AddDays(-30), 80, 80, null) }, Now));

    // ---- time axis -------------------------------------------------------------------------

    [Fact]
    public void Day_ticks_are_every_six_hours_on_the_clock()
    {
        var axis = Build(Array.Empty<BatterySample>()).Axis;     // window 12:00 yesterday .. 12:00 today
        Assert.Equal(new[] { "12:00", "18:00", "00:00", "06:00", "12:00" }, axis.Labels.Select(l => l.Label));
        Assert.Equal(new[] { 0.0, 60.0, 120.0, 180.0, 240.0 }, axis.Labels.Select(l => Math.Round(l.X, 3)));
        Assert.Equal(axis.Labels.Select(l => l.X), axis.Lines);
    }

    [Fact]
    public void Day_ticks_use_am_and_pm_in_twelve_hour_cultures()
    {
        var axis = BatteryChartModel.Build(Array.Empty<BatterySample>(), Now, BatteryChartRange.Day, 240, 100, Utc, new CultureInfo("en-US")).Axis;
        Assert.Equal(new[] { "12 PM", "6 PM", "12 AM", "6 AM", "12 PM" }, axis.Labels.Select(l => l.Label));
    }

    [Fact]
    public void Day_ticks_thin_out_on_a_narrow_chart()
        => Assert.Equal(new[] { "12:00", "00:00", "12:00" }, Build(Array.Empty<BatterySample>(), width: 160).Axis.Labels.Select(l => l.Label));

    [Fact]
    public void Week_ticks_are_weekday_names_centred_on_the_visible_part_of_each_day()
    {
        var axis = Build(Array.Empty<BatterySample>(), BatteryChartRange.Week, width: 700).Axis;
        // Window: Fri 25 Sep 12:00 to Fri 2 Oct 12:00, 100 px a day. Both Fridays are half days.
        Assert.Equal(new[] { "Fri", "Sat", "Sun", "Mon", "Tue", "Wed", "Thu", "Fri" }, axis.Labels.Select(l => l.Label));
        Assert.Equal(25.0, axis.Labels[0].X, 3);
        Assert.Equal(675.0, axis.Labels[^1].X, 3);
        Assert.Equal(7, axis.Lines.Count);                    // seven midnights inside the window
    }

    [Fact]
    public void Week_ticks_skip_a_sliver_of_a_day()
    {
        var early = new DateTimeOffset(2026, 10, 2, 3, 0, 0, TimeSpan.Zero);   // today has only 3 hours so far
        var axis = BatteryChartModel.Build(Array.Empty<BatterySample>(), early, BatteryChartRange.Week, 700, 100, Utc, Inv).Axis;
        Assert.Equal(7, axis.Labels.Count);
        Assert.DoesNotContain(axis.Labels, l => l.X > 690);
    }

    [Fact]
    public void Ticks_follow_the_local_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(-5), "test", "test");   // 07:00 local now
        var axis = BatteryChartModel.Build(Array.Empty<BatterySample>(), Now, BatteryChartRange.Day, 240, 100, zone, Inv).Axis;
        Assert.Equal(new[] { "12:00", "18:00", "00:00", "06:00" }, axis.Labels.Select(l => l.Label));
        Assert.Equal(new[] { 50.0, 110.0, 170.0, 230.0 }, axis.Labels.Select(l => Math.Round(l.X, 3)));
    }

    // ---- sparse history: silent stretches shrink to break markers ----------------------------

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    private static BatteryChartModel Compressed(IReadOnlyList<BatterySample> samples, BatteryChartRange range = BatteryChartRange.Week, double width = 300)
        => Build(samples, range, width, compress: true);

    // The owner's case: a few short sessions spread over a week (Now is Fri 2 Oct 12:00, window starts Fri 25 Sep 12:00).
    private static List<BatterySample> SparseWeek() => new()
    {
        Reading(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero), 80, 70, 60),
        Reading(new DateTimeOffset(2026, 9, 26, 9, 10, 0, TimeSpan.Zero), 78, 70, 60),
        Reading(new DateTimeOffset(2026, 9, 26, 9, 20, 0, TimeSpan.Zero), 76, 68, 60),
        Reading(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero), 50, 50, null),       // a lone reading
        Reading(At(1, 18), 40, 40, 90),
        Reading(At(1, 18, 30), 38, 40, 90),
        Reading(At(2, 8), 90, 90, 100, charging: true),
        Reading(At(2, 10), 100, 100, 100, charging: true),
    };

    [Fact]
    public void A_dense_day_stays_linear()
    {
        var samples = Enumerable.Range(0, 48).Select(i => Reading(Now.AddMinutes(-30 * i), 90, 90, null)).ToList();
        var m = Compressed(samples, BatteryChartRange.Day, 240);
        Assert.False(m.IsCompressed);
        Assert.Empty(m.Breaks);
        Assert.Equal(new[] { "12:00", "18:00", "00:00", "06:00", "12:00" }, m.Axis.Labels.Select(l => l.Label));
        Assert.Equal(new[] { 0.0, 60.0, 120.0, 180.0, 240.0 }, m.Axis.Labels.Select(l => Math.Round(l.X, 3)));
    }

    [Fact]
    public void A_week_with_only_overnight_gaps_stays_linear()
    {
        // Used every day from 08:00 to 20:00 with the PC off overnight: half the week is silent, which is normal.
        var samples = new List<BatterySample>();
        for (int d = 0; d < 8; d++)
            for (int h = 8; h <= 20; h += 2)
            {
                var t = Now.Date.AddDays(-7 + d).AddHours(h);
                if (t <= Now && t >= Now.AddDays(-7)) samples.Add(Reading(new DateTimeOffset(t, TimeSpan.Zero), 80, 80, null));
            }
        var m = Compressed(samples, BatteryChartRange.Week, 700);
        Assert.False(m.IsCompressed);
        Assert.Equal(new[] { "Fri", "Sat", "Sun", "Mon", "Tue", "Wed", "Thu", "Fri" }, m.Axis.Labels.Select(l => l.Label));
    }

    [Fact]
    public void Sparse_sessions_across_a_week_are_compressed_with_break_markers()
    {
        var m = Compressed(SparseWeek());
        Assert.True(m.IsCompressed);
        // Before the first session, then between sessions: four silent stretches. Fri 08:00-12:00 runs on to now.
        Assert.Equal(4, m.Breaks.Count);
        Assert.All(m.Breaks, b => Assert.Equal(ChartTimeMap.BreakWidth, b.X2 - b.X1, 3));
        // The probes stay in time order, inside the plot, and each short session now has real width.
        Assert.Equal(SparseWeek().Count, m.Probes.Count);
        Assert.All(m.Probes, p => Assert.InRange(p.X, 0, 300));
        Assert.Equal(m.Probes.Select(p => p.X).OrderBy(x => x), m.Probes.Select(p => p.X));
        Assert.True(m.Probes[2].X - m.Probes[0].X >= 25, "a 20 minute session must be wider than a sliver");
        Assert.True(m.Probes[5].X - m.Probes[4].X >= 25);
        // No reading falls inside a break marker.
        Assert.All(m.Probes, p => Assert.DoesNotContain(m.Breaks, b => p.X > b.X1 + 0.01 && p.X < b.X2 - 0.01));
    }

    [Fact]
    public void Compression_never_invents_values_between_readings()
    {
        var m = Compressed(SparseWeek());
        var allowed = new[] { 80, 78, 76, 50, 40, 38, 90, 100 }.Select(l => Math.Round(100 * (1 - l / 100.0), 3)).ToHashSet();
        Assert.All(m.Left.SelectMany(s => s.Points), p => Assert.Contains(Math.Round(p.Y, 3), allowed));
        // Lines still break at every silence: one piece per session.
        Assert.Equal(4, m.Left.Count);
    }

    [Fact]
    public void Compressed_axis_names_each_session_by_its_start_and_drops_labels_that_would_overlap()
    {
        var m = Compressed(SparseWeek());
        Assert.Equal("Sat 09:00", m.Axis.Labels[0].Label);
        for (int i = 1; i < m.Axis.Labels.Count; i++)
            Assert.True(m.Axis.Labels[i].X - m.Axis.Labels[i - 1].X >= BatteryChartModel.LabelWidth);
        // Wider chart: more sessions get a name (the lone Sunday reading is still too close to Saturday's label).
        var wide = Compressed(SparseWeek(), width: 600);
        Assert.Equal(new[] { "Sat 09:00", "Thu 18:00", "Fri 08:00" }, wide.Axis.Labels.Select(l => l.Label));
    }

    [Fact]
    public void A_24_hour_view_with_two_short_stretches_is_compressed_and_labelled_by_clock_time()
    {
        var samples = new[]
        {
            Reading(Now.AddHours(-20), 90, 90, null), Reading(Now.AddHours(-19), 80, 80, null),
            Reading(Now.AddHours(-2), 60, 60, null), Reading(Now.AddHours(-1), 55, 55, null),
        };
        var m = Compressed(samples, BatteryChartRange.Day);
        Assert.True(m.IsCompressed);
        Assert.Equal(2, m.Breaks.Count);
        Assert.Equal(new[] { "16:00", "10:00" }, m.Axis.Labels.Select(l => l.Label));
        Assert.Equal(300.0, m.Left[^1].Points[^1].X, 3);     // the last session runs on to now
    }

    [Fact]
    public void An_isolated_reading_is_one_dot_in_the_middle_of_its_own_session()
    {
        var m = Compressed(new[] { Reading(Now.AddDays(-2), 70, 70, 50) });
        Assert.True(m.IsCompressed);
        Assert.Equal(2, m.Breaks.Count);                     // silence before it and after it
        var dot = Assert.Single(m.Left);
        Assert.True(dot.IsDot);
        Assert.Equal(150.0, dot.Points[0].X, 3);
        Assert.Equal(dot.Points[0].X, Assert.Single(m.Probes).X, 3);
        Assert.Equal("Wed 12:00", Assert.Single(m.Axis.Labels).Label);
    }

    [Fact]
    public void Charging_over_a_session_is_a_band_as_wide_as_the_session()
    {
        var m = Compressed(SparseWeek());
        var band = Assert.Single(m.Charging);                // only the Fri 08:00-12:00 session was charging
        Assert.False(band.Instant);
        Assert.Equal(m.Probes[6].X, band.X1, 3);
        Assert.Equal(300.0, band.X2, 3);
    }

    [Fact]
    public void A_single_charging_reading_is_a_marker_not_a_full_height_band()
    {
        var m = Compressed(new[] { Reading(Now.AddDays(-2), 70, 70, 50, charging: true), Reading(Now.AddDays(-4), 80, 80, 50) });
        var band = Assert.Single(m.Charging);
        Assert.True(band.Instant);
        Assert.Equal(band.X1, band.X2, 3);
    }

    [Fact]
    public void Charging_that_stops_inside_a_session_ends_at_the_next_reading()
    {
        var samples = new[]
        {
            Reading(At(2, 6), 40, 40, null), Reading(At(2, 7), 41, 41, null, charging: true),
            Reading(At(2, 8), 60, 60, null, charging: true), Reading(At(2, 9), 70, 70, null),
            Reading(At(2, 10), 70, 70, null),
        };
        var m = Compressed(samples, BatteryChartRange.Week);
        var band = Assert.Single(m.Charging);
        Assert.False(band.Instant);
        Assert.Equal(m.Probes[1].X, band.X1, 3);
        Assert.Equal(m.Probes[3].X, band.X2, 3);
    }

    // ---- summary line ----------------------------------------------------------------------

    [Fact]
    public void Summary_says_not_enough_history_with_no_readings()
        => Assert.Equal("Not enough history yet", BatteryChartSummary.Text(Array.Empty<BatterySample>(), Now));

    [Fact]
    public void Summary_says_charging_when_the_pods_are_charging()
        => Assert.Equal("Charging", BatteryChartSummary.Text(new[] { Reading(Now.AddMinutes(-2), 50, 50, null, charging: true) }, Now));

    [Fact]
    public void Summary_says_no_recent_reading_when_the_last_one_is_old()
    {
        var samples = new[] { Reading(Now.AddHours(-9), 90, 90, null), Reading(Now.AddHours(-8), 80, 80, null), Reading(Now.AddHours(-7), 70, 70, null) };
        Assert.Equal("No recent reading", BatteryChartSummary.Text(samples, Now));
    }

    [Fact]
    public void Summary_does_not_claim_charging_from_an_old_charging_reading()
    {
        var old = new[] { Reading(Now.AddDays(-2), 50, 50, null, charging: true) };
        Assert.Equal("Last seen charging, 2 days ago", BatteryChartSummary.Text(old, Now));
        Assert.Equal("Last seen charging, 3 hr ago", BatteryChartSummary.Text(new[] { Reading(Now.AddHours(-3), 50, 50, null, charging: true) }, Now));
    }

    [Fact]
    public void Summary_charging_reading_is_stale_when_the_pods_were_last_heard_long_ago()
    {
        var samples = new[] { Reading(Now.AddMinutes(-2), 50, 50, null, charging: true) };
        Assert.Equal("Charging", BatteryChartSummary.Text(samples, Now, Now.AddMinutes(-1)));
        Assert.StartsWith("Last seen charging", BatteryChartSummary.Text(samples, Now, Now.AddHours(-5)));
    }

    [Fact]
    public void Drain_estimator_reports_stale_before_charging()
    {
        var old = new[] { Reading(Now.AddDays(-2), 50, 50, null, charging: true) };
        Assert.Equal(DrainEstimator.StaleText, DrainEstimator.Estimate(old, Now).Text);
        var fresh = new[] { Reading(Now.AddMinutes(-2), 50, 50, null, charging: true) };
        Assert.Equal(DrainEstimator.ChargingText, DrainEstimator.Estimate(fresh, Now).Text);
    }

    [Fact]
    public void Summary_gives_the_time_left_at_this_rate()
    {
        var samples = Enumerable.Range(0, 6).Select(i => Reading(Now.AddHours(-5 + i), 90 - i * 10, 90 - i * 10, null)).ToList();
        Assert.Matches(@"^About [\d.]+ (hr|min) left at this rate$", BatteryChartSummary.Text(samples, Now));
    }
}
