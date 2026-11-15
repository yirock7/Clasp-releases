using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Battery;

internal static class B
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A clock a day after <see cref="T0"/>, so reload/flush tests do not depend on today's date.</summary>
    public static Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock => new(T0.AddDays(1));

    public static BatterySample S(DateTimeOffset t, int? l, int? r = null, int? c = null, bool? lc = null, bool? rc = null, BatterySampleSource src = BatterySampleSource.Broadcast) =>
        new(t, l, r ?? l, c, lc, rc ?? lc, null, src);

    public static BatterySample Pod(DateTimeOffset t, int level) => S(t, level, lc: false);

    public static BatterySample Charge(DateTimeOffset t, int level) => S(t, level, lc: true);
}

public class BatteryHistoryTests
{
    private const IdentityConfidence Mine = IdentityConfidence.LikelyYours;

    [Fact]
    public void Load_and_flush_enforce_the_age_limit_on_every_device_not_only_the_one_recorded()
    {
        var store = new InMemoryBatteryHistoryStore();
        var writer = new BatteryHistory(store, null, B.Clock);
        writer.Record("old", B.Pod(B.T0.AddDays(-200), 80), Mine);
        writer.Record("old", B.Pod(B.T0.AddDays(-199), 70), Mine);
        writer.Record("new", B.Pod(B.T0, 60), Mine);
        // Both were accepted (Record only prunes the device being recorded, and "old" came first).
        Assert.Equal(2, writer.Samples("old").Count);
        Assert.True(writer.Flush());

        var flushed = new BatteryHistory(store, null, B.Clock);
        flushed.Load();
        Assert.Empty(flushed.Samples("old"));
        Assert.Single(flushed.Samples("new"));
        Assert.DoesNotContain("old", store.Read(BatteryHistory.StoreName)!);

        // And an old file read much later loses its readings on load, marking itself for a rewrite.
        var later = new BatteryHistory(store, null, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(B.T0.AddDays(120)));
        later.Load();
        Assert.Empty(later.Samples("new"));
        Assert.True(later.IsDirty);
    }

    [Fact]
    public void Clear_forgets_everything_and_flush_writes_the_empty_history()
    {
        var store = new InMemoryBatteryHistoryStore();
        var h = new BatteryHistory(store, null, B.Clock);
        h.Record("a", B.Pod(B.T0, 80), Mine);
        h.Record("b", B.Pod(B.T0, 70), Mine);
        h.Flush();
        h.Clear();
        Assert.Empty(h.Samples("a"));
        Assert.True(h.IsDirty);
        Assert.True(h.Flush());
        var again = new BatteryHistory(store, null, B.Clock);
        again.Load();
        Assert.Empty(again.Samples("a"));
        Assert.Empty(again.Samples("b"));
    }

    [Fact]
    public void An_oversized_file_keeps_only_the_most_recent_devices_on_load()
    {
        var store = new InMemoryBatteryHistoryStore();
        var writer = new BatteryHistory(store, new BatteryHistoryOptions { MaxDevices = 50 }, B.Clock);
        for (int i = 0; i < 12; i++) writer.Record($"d{i}", B.Pod(B.T0.AddMinutes(i), 80), Mine);
        writer.Flush();
        var loaded = new BatteryHistory(store, null, B.Clock); // default cap 8
        loaded.Load();
        Assert.Empty(loaded.Samples("d0"));
        Assert.NotEmpty(loaded.Samples("d11"));
        Assert.Equal(8, Enumerable.Range(0, 12).Count(i => loaded.Samples($"d{i}").Count > 0));
    }

    [Fact]
    public void Appends_only_when_a_value_changes()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore());
        Assert.True(h.Record("d", B.Pod(B.T0, 80), Mine));
        Assert.False(h.Record("d", B.Pod(B.T0.AddMinutes(5), 80), Mine));
        Assert.True(h.Record("d", B.Pod(B.T0.AddMinutes(10), 70), Mine));
        Assert.Equal(2, h.Samples("d").Count);
    }

    [Fact]
    public void A_charging_flag_change_is_a_change()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore());
        h.Record("d", B.Pod(B.T0, 80), Mine);
        Assert.True(h.Record("d", B.Charge(B.T0.AddMinutes(1), 80), Mine));
    }

    [Fact]
    public void Broadcast_samples_need_an_independent_pairing_clue_but_other_sources_do_not()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore());
        Assert.False(h.Record("d", B.Pod(B.T0, 80), IdentityConfidence.Unknown));
        Assert.False(h.Record("d", B.Pod(B.T0, 80), IdentityConfidence.Probable));
        Assert.True(h.Record("d", B.S(B.T0, 80, lc: false, src: BatterySampleSource.Windows), IdentityConfidence.Unknown));
        Assert.True(h.Record("d", B.Pod(B.T0.AddMinutes(1), 70), IdentityConfidence.LikelyYours));
    }

    [Fact]
    public void Ignores_empty_samples_and_samples_older_than_the_latest()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore());
        Assert.False(h.Record("d", B.S(B.T0, null), Mine));
        h.Record("d", B.Pod(B.T0, 80), Mine);
        Assert.False(h.Record("d", B.Pod(B.T0.AddMinutes(-5), 60), Mine));
        Assert.False(h.Record("", B.Pod(B.T0, 60), Mine));
    }

    [Fact]
    public void Devices_are_kept_apart()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore());
        h.Record("a", B.Pod(B.T0, 80), Mine);
        h.Record("b", B.Pod(B.T0, 50), Mine);
        Assert.Equal(80, h.Samples("a")[0].Left);
        Assert.Equal(50, h.Samples("b")[0].Left);
        Assert.Empty(h.Samples("c"));
    }

    [Fact]
    public void Drops_samples_older_than_the_age_limit()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore(), new BatteryHistoryOptions { MaxAge = TimeSpan.FromDays(10) });
        h.Record("d", B.Pod(B.T0, 90), Mine);
        h.Record("d", B.Pod(B.T0.AddDays(5), 80), Mine);
        h.Record("d", B.Pod(B.T0.AddDays(12), 70), Mine);
        Assert.Equal(new[] { 80, 70 }, h.Samples("d").Select(s => s.Left!.Value));
    }

    [Fact]
    public void Caps_samples_per_device_keeping_the_newest()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore(), new BatteryHistoryOptions { MaxSamplesPerDevice = 3 });
        for (int i = 0; i < 6; i++) h.Record("d", B.Pod(B.T0.AddMinutes(i), 100 - i), Mine);
        Assert.Equal(new[] { 97, 96, 95 }, h.Samples("d").Select(s => s.Left!.Value));
    }

    [Fact]
    public void Caps_the_number_of_devices_dropping_the_least_recent()
    {
        var h = new BatteryHistory(new InMemoryBatteryHistoryStore(), new BatteryHistoryOptions { MaxDevices = 2 });
        h.Record("a", B.Pod(B.T0, 80), Mine);
        h.Record("b", B.Pod(B.T0.AddMinutes(1), 80), Mine);
        h.Record("c", B.Pod(B.T0.AddMinutes(2), 80), Mine);
        Assert.Empty(h.Samples("a"));
        Assert.NotEmpty(h.Samples("b"));
        Assert.NotEmpty(h.Samples("c"));
    }

    [Fact]
    public void Round_trips_through_the_store_including_unreported_values_and_odd_ids()
    {
        var store = new InMemoryBatteryHistoryStore();
        var h = new BatteryHistory(store, null, B.Clock);
        const string id = "Bluetooth#Dev,|x&y {1}";
        var a = new BatterySample(B.T0, 80, null, 100, false, null, true, BatterySampleSource.Broadcast);
        var b = new BatterySample(B.T0.AddMinutes(9), 70, 60, null, null, true, null, BatterySampleSource.Driver);
        h.Record(id, a, Mine);
        h.Record(id, b, Mine);
        Assert.True(h.IsDirty);
        Assert.True(h.Flush());
        Assert.False(h.IsDirty);

        var loaded = new BatteryHistory(store, null, B.Clock);
        loaded.Load();
        Assert.Equal(new[] { a, b }, loaded.Samples(id));
    }

    [Fact]
    public void Load_of_missing_or_damaged_data_is_empty_and_skips_bad_lines()
    {
        var store = new InMemoryBatteryHistoryStore();
        var h = new BatteryHistory(store, null, B.Clock);
        h.Load();
        Assert.Empty(h.Samples("d"));

        store.Write(BatteryHistory.StoreName, "garbage");
        h.Load();
        Assert.Empty(h.Samples("d"));

        store.Write(BatteryHistory.StoreName, "clasp-battery-history 1\nDd\n1790000000,80,,,0,0\nnot,a,line\n1790000100,999,,,0,0\n1790000200,70,,,0,0\n");
        h.Load();
        Assert.Equal(new[] { 80, 70 }, h.Samples("d").Select(s => s.Left!.Value));
    }

    [Fact]
    public void Flush_without_changes_does_not_write_and_a_failed_write_stays_dirty()
    {
        var store = new FailingStore();
        var h = new BatteryHistory(store, null, B.Clock);
        Assert.True(h.Flush());
        Assert.Equal(0, store.Writes);
        h.Record("d", B.Pod(B.T0, 80), Mine);
        Assert.False(h.Flush());
        Assert.True(h.IsDirty);
        store.Fail = false;
        Assert.True(h.Flush());
        Assert.False(h.IsDirty);
    }

    private sealed class FailingStore : IBatteryHistoryStore
    {
        public bool Fail = true;
        public int Writes;
        public string? Read(string name) => null;
        public bool Write(string name, string content) { Writes++; return !Fail; }
    }
}

public class DrainEstimatorTests
{
    private static List<BatterySample> Hourly(params int[] levels) =>
        levels.Select((l, i) => B.Pod(B.T0.AddHours(i), l)).ToList();

    [Fact]
    public void Steady_drain_gives_a_range_labelled_as_an_estimate()
    {
        var samples = Hourly(80, 70, 60, 50);           // 10 % per hour
        var now = B.T0.AddHours(3).AddMinutes(5);
        var e = DrainEstimator.Estimate(samples, now);
        Assert.Equal(EstimateKind.Estimate, e.Kind);
        Assert.Equal(TimeSpan.FromHours(4), e.Low);     // 50 / 12.5
        Assert.Equal(TimeSpan.FromHours(8), e.High);    // 60 / 7.5
        Assert.Equal("Estimate: about 4 to 8 hours left", e.Text);
    }

    [Fact]
    public void The_range_is_never_a_single_value()
    {
        var e = DrainEstimator.Estimate(Hourly(100, 90, 80, 70, 60, 50, 40, 30), B.T0.AddHours(7));
        Assert.True(e.High > e.Low);
    }

    [Fact]
    public void One_step_drop_is_not_enough()
    {
        var e = DrainEstimator.Estimate(Hourly(80, 70), B.T0.AddHours(1));
        Assert.Equal(EstimateKind.NotEnoughData, e.Kind);
        Assert.Null(e.Low);
        Assert.Null(e.High);
    }

    [Fact]
    public void Two_step_drops_are_enough()
    {
        var e = DrainEstimator.Estimate(Hourly(80, 70, 60), B.T0.AddHours(2));
        Assert.Equal(EstimateKind.Estimate, e.Kind);
    }

    [Fact]
    public void No_data_says_not_enough_data_without_claiming_a_value()
    {
        var e = DrainEstimator.Estimate(Array.Empty<BatterySample>(), B.T0);
        Assert.Equal(EstimateKind.NotEnoughData, e.Kind);
        HonestyGuard.AssertClaimsNoValue(e.Text);
    }

    [Fact]
    public void Charging_gives_no_estimate()
    {
        var samples = Hourly(80, 70, 60, 50);
        samples.Add(B.Charge(B.T0.AddHours(4), 50));
        var e = DrainEstimator.Estimate(samples, B.T0.AddHours(4));
        Assert.Equal(EstimateKind.NotEnoughData, e.Kind);
        HonestyGuard.AssertClaimsNoValue(e.Text);
    }

    [Fact]
    public void A_stale_reading_gives_no_estimate_but_a_recent_last_heard_time_keeps_it()
    {
        var samples = Hourly(80, 70, 60, 50);
        var late = B.T0.AddHours(3).AddHours(2);
        Assert.Equal(EstimateKind.NotEnoughData, DrainEstimator.Estimate(samples, late).Kind);
        Assert.Equal(EstimateKind.Estimate, DrainEstimator.Estimate(samples, late, lastHeardAt: late.AddMinutes(-1)).Kind);
    }

    [Fact]
    public void A_charge_in_between_splits_the_stretches_so_charging_time_is_not_counted_as_drain()
    {
        var samples = Hourly(80, 70, 60);
        samples.Add(B.Charge(B.T0.AddHours(3), 60));
        samples.Add(B.Charge(B.T0.AddHours(8), 100));
        samples.Add(B.Pod(B.T0.AddHours(9), 90));
        samples.Add(B.Pod(B.T0.AddHours(10), 80));
        samples.Add(B.Pod(B.T0.AddHours(11), 70));
        var e = DrainEstimator.Estimate(samples, B.T0.AddHours(11));
        Assert.Equal(EstimateKind.Estimate, e.Kind);
        Assert.Equal(TimeSpan.FromMinutes(335), e.Low);   // 70 / 12.5 = 5.6 h = 336 min, rounded down to 5 min
    }

    [Fact]
    public void A_long_silence_between_steps_is_not_counted_as_slow_drain()
    {
        var samples = new List<BatterySample>
        {
            B.Pod(B.T0, 90), B.Pod(B.T0.AddHours(1), 80), B.Pod(B.T0.AddHours(12), 70),
        };
        Assert.Equal(EstimateKind.NotEnoughData, DrainEstimator.Estimate(samples, B.T0.AddHours(12)).Kind);
    }

    [Fact]
    public void Uses_the_lowest_pod()
    {
        var samples = Enumerable.Range(0, 4).Select(i => B.S(B.T0.AddHours(i), 90, 80 - (i * 10), lc: false)).ToList();
        var e = DrainEstimator.Estimate(samples, B.T0.AddHours(3));
        Assert.Equal(EstimateKind.Estimate, e.Kind);
        Assert.Equal(TimeSpan.FromHours(4), e.Low);     // lowest pod is at 50 %
    }

    [Fact]
    public void Short_ranges_are_written_in_minutes()
    {
        var samples = new List<BatterySample> { B.Pod(B.T0, 30), B.Pod(B.T0.AddMinutes(30), 20), B.Pod(B.T0.AddMinutes(60), 10) };
        var e = DrainEstimator.Estimate(samples, B.T0.AddMinutes(60));
        Assert.Equal(EstimateKind.Estimate, e.Kind);
        Assert.Contains("minutes left", e.Text);
        Assert.StartsWith("Estimate:", e.Text);
    }
}

public class LowBatteryAlertPolicyTests
{
    private const IdentityConfidence Mine = IdentityConfidence.LikelyYours;

    [Fact]
    public void Alerts_once_when_the_lowest_pod_reaches_the_threshold()
    {
        var p = new LowBatteryAlertPolicy();
        Assert.Null(p.Evaluate("d", Mine, 40, 50, false, 20));
        Assert.Null(p.Evaluate("d", Mine, 30, 50, false, 20));
        var alert = p.Evaluate("d", Mine, 20, 50, false, 20);
        Assert.NotNull(alert);
        Assert.Equal(20, alert!.Percent);
        Assert.Contains("left earbud", alert.Body);
        Assert.Null(p.Evaluate("d", Mine, 10, 50, false, 20));
    }

    [Fact]
    public void Uses_the_right_pod_when_it_is_lower_and_both_when_equal()
    {
        Assert.Contains("right earbud", new LowBatteryAlertPolicy().Evaluate("d", Mine, 50, 10, false, 20)!.Body);
        Assert.Contains("Both earbuds", new LowBatteryAlertPolicy().Evaluate("d", Mine, 10, 10, false, 20)!.Body);
    }

    [Fact]
    public void Zero_threshold_turns_alerts_off()
    {
        Assert.Null(new LowBatteryAlertPolicy().Evaluate("d", Mine, 10, 10, false, 0));
    }

    [Fact]
    public void Unknown_identity_never_alerts_and_does_not_use_up_the_alert()
    {
        var p = new LowBatteryAlertPolicy();
        Assert.Null(p.Evaluate("d", IdentityConfidence.Unknown, 10, 10, false, 20));
        Assert.Null(p.Evaluate("d", IdentityConfidence.Probable, 10, 10, false, 20));
        Assert.NotNull(p.Evaluate("d", Mine, 10, 10, false, 20));
    }

    [Fact]
    public void Rearms_only_after_charging_above_threshold_plus_ten()
    {
        var clock = new ManualTimeProvider();
        var p = new LowBatteryAlertPolicy(clock);
        Assert.NotNull(p.Evaluate("d", Mine, 10, 60, false, 20));
        Assert.Null(p.Evaluate("d", Mine, 20, 60, true, 20));    // charging, still low
        Assert.Null(p.Evaluate("d", Mine, 30, 60, true, 20));    // exactly threshold + 10: not above
        clock.Advance(TimeSpan.FromHours(2));                    // the cooldown must also have passed
        Assert.Null(p.Evaluate("d", Mine, 40, 60, true, 20));    // re-armed here
        Assert.NotNull(p.Evaluate("d", Mine, 20, 60, false, 20));
    }

    [Fact]
    public void A_higher_reading_without_ever_seeing_charging_does_not_rearm()
    {
        var p = new LowBatteryAlertPolicy();
        Assert.NotNull(p.Evaluate("d", Mine, 10, 60, false, 20));
        Assert.Null(p.Evaluate("d", Mine, null, 60, false, 20));  // low pod simply stopped reporting
        Assert.Null(p.Evaluate("d", Mine, 10, 60, false, 20));
    }

    [Fact]
    public void No_alert_while_charging_and_none_without_a_reading()
    {
        var p = new LowBatteryAlertPolicy();
        Assert.Null(p.Evaluate("d", Mine, 10, 10, true, 20));
        Assert.Null(p.Evaluate("d", Mine, null, null, false, 20));
        Assert.NotNull(p.Evaluate("d", Mine, 10, 10, false, 20));
    }

    [Fact]
    public void Each_device_has_its_own_state()
    {
        var p = new LowBatteryAlertPolicy();
        Assert.NotNull(p.Evaluate("a", Mine, 10, 10, false, 20));
        Assert.NotNull(p.Evaluate("b", Mine, 10, 10, false, 20));
        Assert.Null(p.Evaluate("a", Mine, 10, 10, false, 20));
    }

    [Fact]
    public void Single_reported_pod_is_used()
    {
        Assert.NotNull(new LowBatteryAlertPolicy().Evaluate("d", Mine, null, 10, false, 20));
    }
}

public class BatteryWearReportTests
{
    // One charge per day, then drain at one step per stepMinutes.
    private static List<BatterySample> Days(int from, int to, int stepMinutes)
    {
        var list = new List<BatterySample>();
        for (int d = from; d < to; d++)
        {
            var day = B.T0.AddDays(d);
            list.Add(B.Charge(day, 100));
            for (int i = 1; i <= 5; i++) list.Add(B.Pod(day.AddMinutes(i * stepMinutes), 100 - (i * 10)));
        }
        return list;
    }

    [Fact]
    public void Not_enough_history_says_so_without_numbers()
    {
        var samples = Days(0, 10, 60);
        var r = BatteryWearReport.Build(samples, B.T0.AddDays(10));
        Assert.Equal(WearReportKind.NotEnoughData, r.Kind);
        HonestyGuard.AssertClaimsNoValue(r.Text);
    }

    [Fact]
    public void Empty_history_is_not_enough_data()
    {
        Assert.Equal(WearReportKind.NotEnoughData, BatteryWearReport.Build(Array.Empty<BatterySample>(), B.T0).Kind);
    }

    [Fact]
    public void Long_history_with_a_gap_in_the_middle_still_needs_enough_recent_segments()
    {
        var samples = Days(0, 20, 60);
        var r = BatteryWearReport.Build(samples, B.T0.AddDays(60));
        Assert.Equal(WearReportKind.NotEnoughData, r.Kind);
    }

    [Fact]
    public void Faster_recent_drain_is_reported_in_plain_language()
    {
        var samples = Days(0, 14, 60);
        samples.AddRange(Days(26, 42, 30));
        var r = BatteryWearReport.Build(samples, B.T0.AddDays(42));
        Assert.Equal(WearReportKind.Comparable, r.Kind);
        Assert.Equal(100, r.ChangePercent);
        Assert.Contains("faster", r.Text);
        Assert.Contains("not a diagnosis", r.Text);
    }

    [Fact]
    public void Similar_drain_is_called_about_the_same()
    {
        var samples = Days(0, 14, 60);
        samples.AddRange(Days(26, 42, 60));
        var r = BatteryWearReport.Build(samples, B.T0.AddDays(42));
        Assert.Equal(WearReportKind.Comparable, r.Kind);
        Assert.Contains("about the same", r.Text);
    }

    [Fact]
    public void Slower_recent_drain_is_described_as_slower()
    {
        var samples = Days(0, 14, 30);
        samples.AddRange(Days(26, 42, 60));
        var r = BatteryWearReport.Build(samples, B.T0.AddDays(42));
        Assert.Contains("slower", r.Text);
        Assert.Equal(-50, r.ChangePercent);
    }
}

public class LastSeenTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-40, SignalBucket.Near)]
    [InlineData(-59, SignalBucket.Near)]
    [InlineData(-60, SignalBucket.InRange)]
    [InlineData(-79, SignalBucket.InRange)]
    [InlineData(-80, SignalBucket.Far)]
    [InlineData(-95, SignalBucket.Far)]
    public void Buckets_follow_the_thresholds(short rssi, SignalBucket expected) =>
        Assert.Equal(expected, LastSeenTracker.BucketFor(rssi));

    [Fact]
    public void Unknown_signal_has_no_bucket() => Assert.Equal(SignalBucket.Unknown, LastSeenTracker.BucketFor(null));

    [Fact]
    public void Never_heard_shows_nothing()
    {
        Assert.Null(new LastSeenTracker(new InMemoryBatteryHistoryStore()).Text("d", Now));
    }

    [Fact]
    public void Unknown_identity_is_not_recorded()
    {
        var t = new LastSeenTracker(new InMemoryBatteryHistoryStore());
        Assert.False(t.Observe("d", IdentityConfidence.Unknown, -40, Now));
        Assert.Null(t.Text("d", Now));
    }

    [Fact]
    public void Describes_age_and_signal()
    {
        var t = new LastSeenTracker(new InMemoryBatteryHistoryStore());
        t.Observe("d", IdentityConfidence.LikelyYours, -50, Now);
        Assert.Equal("Last seen 10 min ago · near", t.Text("d", Now.AddMinutes(10)));
        Assert.Equal("Last seen just now · near", t.Text("d", Now.AddSeconds(20)));
        Assert.Equal("Last seen 1 min ago · near", t.Text("d", Now.AddMinutes(1)));
        Assert.Equal("Last seen 3 hr ago · near", t.Text("d", Now.AddHours(3)));
        Assert.Equal("Last seen 2 days ago · near", t.Text("d", Now.AddDays(2)));
    }

    [Fact]
    public void Other_buckets_and_missing_signal_have_their_own_wording()
    {
        var t = new LastSeenTracker(new InMemoryBatteryHistoryStore());
        t.Observe("a", IdentityConfidence.LikelyYours, -70, Now);
        t.Observe("b", IdentityConfidence.LikelyYours, -90, Now);
        t.Observe("c", IdentityConfidence.LikelyYours, null, Now);
        var later = Now.AddMinutes(5);
        Assert.Equal("Last seen 5 min ago · in range", t.Text("a", later));
        Assert.Equal("Last seen 5 min ago · far away", t.Text("b", later));
        Assert.Equal("Last seen 5 min ago", t.Text("c", later));
    }

    [Fact]
    public void Writes_are_throttled_but_a_bucket_change_is_recorded()
    {
        var t = new LastSeenTracker(new InMemoryBatteryHistoryStore());
        Assert.True(t.Observe("d", IdentityConfidence.LikelyYours, -50, Now));
        Assert.False(t.Observe("d", IdentityConfidence.LikelyYours, -50, Now.AddSeconds(10)));
        Assert.True(t.Observe("d", IdentityConfidence.LikelyYours, -70, Now.AddSeconds(20)));
        Assert.True(t.Observe("d", IdentityConfidence.LikelyYours, -70, Now.AddMinutes(2)));
        Assert.False(t.Observe("d", IdentityConfidence.LikelyYours, -70, Now));     // clock went back
    }

    [Fact]
    public void Persists_through_the_store()
    {
        var store = new InMemoryBatteryHistoryStore();
        var t = new LastSeenTracker(store);
        t.Observe("Bluetooth#a,b", IdentityConfidence.LikelyYours, -50, Now);
        Assert.True(t.Flush());
        var loaded = new LastSeenTracker(store);
        loaded.Load();
        Assert.Equal(new LastSeen(Now, SignalBucket.Near), loaded.Get("Bluetooth#a,b"));
        loaded.Load();
        Assert.False(loaded.IsDirty);
    }

    [Fact]
    public void Damaged_data_loads_as_nothing()
    {
        var store = new InMemoryBatteryHistoryStore();
        store.Write(LastSeenTracker.StoreName, "nope");
        var t = new LastSeenTracker(store);
        t.Load();
        Assert.Null(t.Get("d"));
        store.Write(LastSeenTracker.StoreName, "clasp-last-seen 1\nd,notanumber,1\nd,100,9\n");
        t.Load();
        Assert.Null(t.Get("d"));
    }

    [Fact]
    public void Loader_rejects_unix_seconds_outside_DateTimeOffset_range_and_accepts_the_minimum()
    {
        var store = new InMemoryBatteryHistoryStore();
        var below = DateTimeOffset.MinValue.ToUnixTimeSeconds() - 1;
        var above = DateTimeOffset.MaxValue.ToUnixTimeSeconds() + 1;
        store.Write(LastSeenTracker.StoreName,
            $"clasp-last-seen 1\nbelow,{below},1\nabove,{above},1\nminimum,{DateTimeOffset.MinValue.ToUnixTimeSeconds()},1\nmaximum,{DateTimeOffset.MaxValue.ToUnixTimeSeconds()},1\nbefore-epoch,-1,1\n");
        var tracker = new LastSeenTracker(store);

        tracker.Load();

        Assert.Null(tracker.Get("below"));
        Assert.Null(tracker.Get("above"));
        Assert.Equal(new LastSeen(DateTimeOffset.MinValue, SignalBucket.Far), tracker.Get("minimum"));
        Assert.Equal(new LastSeen(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.MaxValue.ToUnixTimeSeconds()), SignalBucket.Far), tracker.Get("maximum"));
        Assert.Equal(new LastSeen(DateTimeOffset.FromUnixTimeSeconds(-1), SignalBucket.Far), tracker.Get("before-epoch"));
    }
}
