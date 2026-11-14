using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Ble;

public class BroadcastDisagreementTests
{
    private const int Tol = 10;
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static PodReading R(int? left = 80, int? right = 70, int? @case = 50, byte? lid = 3, ushort model = 0x2720)
        => new(model, left, right, @case, lid);

    [Fact]
    public void Siblings_have_no_disagreement()
        => Assert.Null(BroadcastSiblings.Disagreement(R(), R(left: 75, right: 72, @case: 55), Tol));

    [Fact]
    public void Model_is_named_with_both_ids()
        => Assert.Equal("model 0x2720 vs 0x2721", BroadcastSiblings.Disagreement(R(), R(model: 0x2721), Tol));

    [Fact]
    public void Lid_counter_is_named_with_both_values()
        => Assert.Equal("lid counter 3 vs 4", BroadcastSiblings.Disagreement(R(), R(lid: 4), Tol));

    [Fact]
    public void Case_level_is_named_with_both_values()
        => Assert.Equal("case 50% vs 90%", BroadcastSiblings.Disagreement(R(), R(@case: 90), Tol));

    [Fact]
    public void Left_and_right_are_named_with_both_pairs_and_unknown_as_question_mark()
        => Assert.Equal("left/right 80%/70% vs 20%/?", BroadcastSiblings.Disagreement(R(), R(left: 20, right: null), Tol));

    [Fact]
    public void Nothing_to_compare_is_said_plainly()
        => Assert.Equal("no battery level to compare",
            BroadcastSiblings.Disagreement(R(left: null, right: null, @case: null), R(), Tol));

    [Fact]
    public void Disagreement_is_null_exactly_when_the_readings_are_siblings()
    {
        int?[] levels = [null, 20, 55, 80];
        byte?[] lids = [null, 3, 4];
        foreach (var l1 in levels) foreach (var r1 in levels) foreach (var c1 in levels) foreach (var lid1 in lids)
        foreach (var l2 in levels) foreach (var r2 in levels) foreach (var c2 in levels) foreach (var lid2 in lids)
        {
            var a = R(l1, r1, c1, lid1);
            var b = R(l2, r2, c2, lid2);
            Assert.Equal(BroadcastSiblings.AreSiblings(a, b, Tol), BroadcastSiblings.Disagreement(a, b, Tol) is null);
        }
    }

    [Fact]
    public void Ambiguous_reason_keeps_its_start_and_adds_the_field_that_differed()
    {
        var policy = new BroadcastAttributionPolicy();
        policy.Evaluate(Payload.Sighting(Payload.Build(), address: 1), Parse(Payload.Build()), true, T0);
        var second = Payload.Build(pods: 0x11);
        var d = policy.Evaluate(Payload.Sighting(second, address: 2), Parse(second), true, T0.AddSeconds(3));

        Assert.Equal(AttributionVerdict.Ambiguous, d.Verdict);
        Assert.StartsWith("2 possible sources heard nearby; not choosing one.", d.Reason, StringComparison.Ordinal);
        Assert.Contains("Not the same pair: left/right", d.Reason, StringComparison.Ordinal);
        Assert.Equal("2 possible sources heard nearby; not choosing one.", d.ReasonKey);
        Assert.DoesNotContain("0xAABB", d.Reason, StringComparison.OrdinalIgnoreCase); // no address
    }

    [Fact]
    public void Ambiguous_reason_names_a_lid_counter_difference()
    {
        var policy = new BroadcastAttributionPolicy();
        var a = Payload.Build(status: 0x62, lid: 3); // both buds in the case: their counters must agree
        var b = Payload.Build(status: 0x62, lid: 4);
        policy.Evaluate(Payload.Sighting(a, address: 1), Parse(a), true, T0);
        var d = policy.Evaluate(Payload.Sighting(b, address: 2), Parse(b), true, T0.AddSeconds(1));

        Assert.Equal(AttributionVerdict.Ambiguous, d.Verdict);
        Assert.EndsWith("Not the same pair: lid counter 3 vs 4.", d.Reason, StringComparison.Ordinal);
    }

    private static ProximityMessage? Parse(byte[] data)
        => ProximityMessageParser.Parse(ProximityMessage.AppleCompanyId, data);
}
