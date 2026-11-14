using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Ble;

public class BroadcastSiblingsTests
{
    private static PodReading Reading(int? left, int? right, int? @case = null, byte? lid = null)
        => new(Payload.ProThree, left, right, @case, lid);

    [Fact]
    public void Siblings_group_together_whichever_side_each_bud_calls_left()
    {
        Assert.True(BroadcastSiblings.AreSiblings(Reading(100, 50), Reading(100, 50), 10));
        Assert.True(BroadcastSiblings.AreSiblings(Reading(100, 50), Reading(50, 100), 10)); // the buds' sides swapped
        Assert.Equal(1, BroadcastSiblings.CountGroups([Reading(100, 50), Reading(50, 100)], 10));
    }

    [Fact]
    public void A_pair_with_truly_different_levels_is_not_a_sibling_either_way()
    {
        Assert.False(BroadcastSiblings.AreSiblings(Reading(100, 50), Reading(50, 30), 10));
        Assert.Equal(2, BroadcastSiblings.CountGroups([Reading(100, 50), Reading(50, 30)], 10));
    }

    [Fact]
    public void Mixed_straight_and_swapped_matches_do_not_combine()
    {
        // Left matches straight, right only matches swapped: that is neither orientation.
        Assert.False(BroadcastSiblings.AreSiblings(Reading(100, 50), Reading(100, 100), 10));
    }

    [Fact]
    public void One_known_side_each_on_opposite_sides_is_a_sibling_via_the_swapped_orientation()
    {
        Assert.True(BroadcastSiblings.AreSiblings(Reading(100, null), Reading(null, 100), 10));
        Assert.Equal(1, BroadcastSiblings.CountGroups([Reading(100, null), Reading(null, 100)], 10));
    }

    [Fact]
    public void One_known_side_each_with_swapped_levels_that_differ_compares_nothing()
    {
        // Straight agrees but compares nothing; swapped compares 100 vs 50 and disagrees. No case: nothing compared.
        Assert.False(BroadcastSiblings.AreSiblings(Reading(100, null), Reading(null, 50), 10));
    }

    [Fact]
    public void Fully_known_swapped_and_equal_pairs_keep_their_old_results()
    {
        Assert.True(BroadcastSiblings.AreSiblings(Reading(100, 50), Reading(50, 100), 10));
        Assert.False(BroadcastSiblings.AreSiblings(Reading(100, 100), Reading(50, 50), 10));
    }

    [Fact]
    public void Case_and_lid_rules_still_apply()
    {
        Assert.False(BroadcastSiblings.AreSiblings(Reading(100, 100, 70), Reading(100, 100, 30), 10));
        Assert.False(BroadcastSiblings.AreSiblings(Reading(100, 100, 70, 1), Reading(100, 100, 70, 2), 10));
        Assert.True(BroadcastSiblings.AreSiblings(Reading(100, 100, 70, 1), Reading(100, 100, null, 0), 10)); // in-ear: lid not comparable
        Assert.False(BroadcastSiblings.AreSiblings(Reading(null, null, null), Reading(null, null, null), 10)); // nothing to compare
    }

    public static TheoryData<int, int, int> AllOrders() => new()
    {
        { 0, 1, 2 }, { 0, 2, 1 }, { 1, 0, 2 }, { 1, 2, 0 }, { 2, 0, 1 }, { 2, 1, 0 },
    };

    [Theory]
    [MemberData(nameof(AllOrders))]
    public void A_partial_reading_cannot_bridge_two_readings_that_conflict(int first, int second, int third)
    {
        PodReading[] readings =
        [
            Reading(100, 100, 70, 1),   // case open, case 70
            Reading(100, 100, null, 0), // in-ear: no case level, compatible with both
            Reading(100, 100, 30, 2),   // case open, case 30: conflicts with the first
        ];
        // The partial reading is a sibling of each, but the two full readings are not siblings of each other.
        Assert.True(BroadcastSiblings.AreSiblings(readings[0], readings[1], 10));
        Assert.True(BroadcastSiblings.AreSiblings(readings[1], readings[2], 10));
        Assert.False(BroadcastSiblings.AreSiblings(readings[0], readings[2], 10));

        Assert.Equal(2, BroadcastSiblings.CountGroups([readings[first], readings[second], readings[third]], 10));
    }

    [Fact]
    public void Two_case_open_buds_and_an_in_ear_reading_with_compatible_levels_are_one_pair()
    {
        var budA = Reading(100, 90, 70, 1);
        var budB = Reading(90, 100, 70, 1); // sides swapped, as the other bud reports them
        var inEar = Reading(100, 90, null, 0);

        Assert.Equal(1, BroadcastSiblings.CountGroups([budA, budB, inEar], 10));
        Assert.Equal(1, BroadcastSiblings.CountGroups([inEar, budA, budB], 10));
        Assert.Equal(1, BroadcastSiblings.CountGroups([budB, inEar, budA], 10));
    }
}
