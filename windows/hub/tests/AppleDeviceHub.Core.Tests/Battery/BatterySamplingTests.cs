using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Tests.Battery;

public class BatterySamplingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static BatteryState State(int? left, int? right, DateTimeOffset podsAt, int? @case = null, DateTimeOffset? caseAt = null, bool? caseCharging = null)
        => new(BatteryComponent.From(left, false, podsAt), BatteryComponent.From(right, false, podsAt),
            BatteryComponent.From(@case, caseCharging, caseAt ?? podsAt));

    [Fact]
    public void A_reading_whose_parts_share_a_time_keeps_all_three()
    {
        var sample = BatterySampling.FromState(State(100, 90, T0, 70, T0, caseCharging: true), BatterySampleSource.Broadcast)!;

        Assert.Equal(T0, sample.Time);
        Assert.Equal((100, 90, 70), (sample.Left, sample.Right, sample.Case));
        Assert.True(sample.CaseCharging);
    }

    [Fact]
    public void A_retained_case_level_is_not_stored_as_measured_with_the_in_ear_reading()
    {
        // Lid-open reading at T0 (C70), then the buds in the ears 5 minutes later: the display keeps C70, history must not.
        var inEar = State(90, 90, T0.AddMinutes(5), 70, T0, caseCharging: true);

        var sample = BatterySampling.FromState(inEar, BatterySampleSource.Broadcast)!;

        Assert.Equal(T0.AddMinutes(5), sample.Time);
        Assert.Equal((90, 90), (sample.Left, sample.Right));
        Assert.Null(sample.Case);
        Assert.Null(sample.CaseCharging);
    }

    [Fact]
    public void A_case_level_remembered_from_hours_ago_is_not_a_sample_even_without_a_charging_flag()
    {
        var remembered = State(90, 90, T0.AddHours(3), 60, T0);   // case from an earlier run, charging unknown

        var sample = BatterySampling.FromState(remembered, BatterySampleSource.Broadcast)!;

        Assert.Null(sample.Case);
        Assert.Null(sample.CaseCharging);
        Assert.Equal((90, 90), (sample.Left, sample.Right));
    }

    [Fact]
    public void Parts_a_few_seconds_apart_still_count_as_one_reading()
    {
        var sample = BatterySampling.FromState(State(100, 100, T0.AddSeconds(2), 70, T0), BatterySampleSource.Broadcast)!;
        Assert.Equal(70, sample.Case);
    }

    [Fact]
    public void Only_a_case_reading_leaves_the_old_pod_levels_out()
    {
        var sample = BatterySampling.FromState(State(100, 100, T0, 60, T0.AddMinutes(2)), BatterySampleSource.Broadcast)!;
        Assert.Null(sample.Left);
        Assert.Null(sample.Right);
        Assert.Equal(60, sample.Case);
    }

    [Fact]
    public void Driver_parts_are_reported_together_so_all_are_kept()
    {
        var sample = BatterySampling.FromState(State(80, 80, T0.AddMinutes(1), 50, T0), BatterySampleSource.Driver)!;
        Assert.Equal(50, sample.Case);
    }

    [Fact]
    public void Nothing_reported_gives_no_sample()
        => Assert.Null(BatterySampling.FromState(BatteryState.Unknown, BatterySampleSource.Broadcast));

    [Fact]
    public void History_charts_and_estimates_handle_a_null_case_without_a_drop_or_a_gap()
    {
        var store = new InMemoryBatteryHistoryStore();
        var history = new BatteryHistory(store, null, B.Clock);
        history.Record("d", BatterySampling.FromState(State(100, 100, T0, 70, T0), BatterySampleSource.Driver)!);
        history.Record("d", BatterySampling.FromState(State(90, 90, T0.AddMinutes(5), 70, T0), BatterySampleSource.Broadcast)!,
            Core.Ble.IdentityConfidence.LikelyYours);
        Assert.Equal(2, history.Samples("d").Count);
        Assert.Null(history.Samples("d")[1].Case);

        var chart = BatteryChartModel.Build(history.Samples("d"), T0.AddMinutes(10), BatteryChartRange.Day, 100, 50, TimeZoneInfo.Utc, System.Globalization.CultureInfo.InvariantCulture);
        var caseLine = Assert.Single(chart.Case);                // the case line holds its last measured level, unbroken
        Assert.All(caseLine.Points, point => Assert.Equal(15, point.Y, 3)); // 70 % of a 50 high chart: never a drop to 0

        history.Flush();
        var reloaded = new BatteryHistory(store, null, B.Clock);
        reloaded.Load();
        Assert.Null(reloaded.Samples("d")[1].Case); // an empty field round-trips as "not reported"
        _ = DrainEstimator.Estimate(reloaded.Samples("d"), T0.AddMinutes(10)); // reads pod levels only; a null case cannot matter
    }
}
