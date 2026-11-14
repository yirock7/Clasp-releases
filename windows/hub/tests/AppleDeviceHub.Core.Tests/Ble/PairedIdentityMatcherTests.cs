using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;

namespace AppleDeviceHub.Core.Tests.Ble;

public class PairedIdentityMatcherTests
{
    private const ushort ProThree = Payload.ProThree;
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // Synthetic, upstream-assumed layout (see BleTestSupport). Nothing here was captured from real AirPods.
    private static ProximityMessage Message(ushort model = ProThree, byte pods = 0x58, byte lid = 0x0B, byte flags = 0x47)
        => ProximityMessageParser.Parse(Payload.Build(model: model, pods: pods, lid: lid, flagsAndCase: flags))!;

    /// <summary>A second pair of the same model with clearly different pod batteries: not the sibling bud of <see cref="Message"/>.</summary>
    private static ProximityMessage Rival() => Message(pods: 0x11);

    /// <summary>Latches a source at <paramref name="at"/> the way a real connect does: a Windows transition lines up with its first appearance.</summary>
    private static void LatchByTiming(PairedIdentityMatcher matcher, DateTimeOffset at)
    {
        matcher.ObserveWindowsConnection(true, at.AddSeconds(-10)); // baseline
        matcher.ObserveWindowsConnection(false, at);
    }

    private static PairedIdentityMatcher Matcher(ushort? paired = ProThree, PairedIdentityOptions? options = null)
    {
        var matcher = new PairedIdentityMatcher(options);
        matcher.SetPairedModel(paired);
        return matcher;
    }

    [Fact]
    public void Without_a_paired_model_everything_is_unknown()
    {
        var matcher = Matcher(paired: null);
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(1, Message(), T0));
    }

    [Fact]
    public void A_different_model_is_unknown_even_when_alone()
    {
        var matcher = Matcher();
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(1, Message(model: Payload.ProTwoUsbC), T0));
    }

    [Fact]
    public void A_single_model_match_is_probable()
    {
        var matcher = Matcher();
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(), T0));
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(), T0.AddSeconds(4)));
    }

    [Fact]
    public void A_different_model_nearby_does_not_make_a_match_ambiguous()
    {
        var matcher = Matcher();
        matcher.Observe(1, Message(), T0);
        matcher.Observe(2, Message(model: Payload.ProTwoUsbC), T0.AddSeconds(1));
        Assert.Equal(IdentityConfidence.Probable, matcher.ConfidenceOf(1, T0.AddSeconds(2)));
    }

    [Fact]
    public void Two_matching_sources_heard_at_once_are_both_unknown()
    {
        var matcher = Matcher();
        matcher.Observe(1, Message(), T0);
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(2, Rival(), T0.AddSeconds(2)));
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0.AddSeconds(2)));
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(2, T0.AddSeconds(2)));
    }

    [Fact]
    public void A_second_pair_bridged_by_a_partial_reading_does_not_inherit_the_first_pairs_confidence()
    {
        var matcher = Matcher();
        matcher.ObserveWindowsConnection(false, T0.AddSeconds(-30)); // baseline
        matcher.ObserveWindowsConnection(true, T0);                  // Windows connects

        // Pods 100/100 throughout. Pair one: case 70, lid 1 (latched by the timing clue), plus a partial reading (no case level).
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(1, Message(pods: 0xAA, flags: 0x47, lid: 1), T0.AddSeconds(1)));
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(2, Message(pods: 0xAA, flags: 0x4F, lid: 0), T0.AddSeconds(2)));
        matcher.Observe(1, Message(pods: 0xAA, flags: 0x47, lid: 1), T0.AddSeconds(7));
        matcher.Observe(2, Message(pods: 0xAA, flags: 0x4F, lid: 0), T0.AddSeconds(7));

        // A conflicting pair (case 30, lid 2) fits only the partial reading: three sources, two pairs, so never pick.
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(3, Message(pods: 0xAA, flags: 0x43, lid: 2), T0.AddSeconds(8)));
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0.AddSeconds(8)));

        // Once the first pair is quiet, the second stands alone: Probable, not the first pair's LikelyYours.
        matcher.Observe(3, Message(pods: 0xAA, flags: 0x43, lid: 2), T0.AddSeconds(14));
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(3, Message(pods: 0xAA, flags: 0x43, lid: 2), T0.AddSeconds(20)));
    }

    [Fact]
    public void Ambiguity_ends_when_the_other_source_goes_quiet()
    {
        var matcher = Matcher();
        matcher.Observe(1, Message(), T0);
        matcher.Observe(2, Message(), T0.AddSeconds(2));
        matcher.Observe(2, Message(), T0.AddSeconds(8));
        matcher.Observe(2, Message(), T0.AddSeconds(14));

        // Source 1 was last heard 14 s ago: outside the 10 s window, so source 2 stands alone again.
        Assert.Equal(IdentityConfidence.Probable, matcher.ConfidenceOf(2, T0.AddSeconds(14)));
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0.AddSeconds(14)));
    }

    [Fact]
    public void Connection_timing_near_the_first_sighting_is_likely_yours()
    {
        var matcher = Matcher();
        matcher.ObserveWindowsConnection(false, T0.AddSeconds(-30)); // baseline
        matcher.ObserveWindowsConnection(true, T0);                  // Windows connects
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(1, Message(), T0.AddSeconds(2)));
    }

    [Fact]
    public void Connection_timing_near_a_state_change_is_likely_yours()
    {
        var matcher = Matcher();
        matcher.ObserveWindowsConnection(true, T0.AddSeconds(-60)); // baseline: already connected
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(), T0.AddSeconds(-40)));
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(), T0.AddSeconds(-20)));

        // The case lid counter changes (pods leave the case) and Windows drops the link a moment later.
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(lid: 0x0C), T0));
        matcher.ObserveWindowsConnection(false, T0.AddSeconds(3));
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.ConfidenceOf(1, T0.AddSeconds(3)));
    }

    [Fact]
    public void A_connection_change_far_from_any_broadcast_change_is_not_a_clue()
    {
        var matcher = Matcher();
        matcher.Observe(1, Message(), T0);
        matcher.Observe(1, Message(), T0.AddSeconds(5));
        matcher.ObserveWindowsConnection(false, T0.AddSeconds(5)); // baseline only
        matcher.ObserveWindowsConnection(true, T0.AddSeconds(9));  // 9 s after the source appeared
        Assert.Equal(IdentityConfidence.Probable, matcher.ConfidenceOf(1, T0.AddSeconds(9)));
    }

    [Fact]
    public void The_first_windows_state_is_a_baseline_not_a_transition()
    {
        var matcher = Matcher();
        matcher.ObserveWindowsConnection(true, T0);
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(), T0.AddSeconds(1)));
    }

    [Fact]
    public void Battery_agreement_alone_is_likely_by_battery_and_never_latches()
    {
        var matcher = Matcher();
        var message = Message();
        int pod = message.LeftBatteryPercent ?? message.RightBatteryPercent
            ?? throw new InvalidOperationException("synthetic payload has no pod battery");
        matcher.SetWindowsBattery(pod + 10, T0); // within one 10 % step
        var rating = matcher.Observe(1, message, T0);
        Assert.Equal(IdentityConfidence.LikelyByBattery, rating);
        Assert.True(rating.HasIndependentClue());
        Assert.False(rating.IsLatched());

        // Once Windows no longer agrees the match is gone: nothing was remembered.
        matcher.SetWindowsBattery(pod >= 50 ? 0 : 100, T0.AddSeconds(1));
        Assert.Equal(IdentityConfidence.Probable, matcher.ConfidenceOf(1, T0.AddSeconds(1)));
    }

    [Fact]
    public void A_forged_source_cycling_every_level_is_not_latched_by_battery()
    {
        var matcher = Matcher();
        matcher.SetWindowsBattery(80, T0);
        for (int level = 0; level <= 9; level++)
            matcher.Observe(1, Message(pods: (byte)(level * 0x11)), T0.AddSeconds(level)); // passes 80 % on the way
        // It now reports a level far from Windows' 80 %: the earlier agreement left nothing behind.
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(pods: 0x11), T0.AddSeconds(12)));
    }

    [Fact]
    public void One_agreeing_sibling_cannot_carry_the_battery_clue_for_a_disagreeing_one()
    {
        var matcher = Matcher();
        matcher.SetWindowsBattery(90, T0);
        // 80/50 agrees with Windows' 90 (80 is within a step); 70/50 is a sibling of it but 20 away.
        Assert.Equal(IdentityConfidence.LikelyByBattery, matcher.Observe(1, Message(pods: 0x58), T0));
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(2, Message(pods: 0x57), T0.AddSeconds(1)));
        Assert.Equal(IdentityConfidence.Probable, matcher.ConfidenceOf(1, T0.AddSeconds(1)));
    }

    [Fact]
    public void A_sibling_already_latched_by_timing_latches_the_group()
    {
        var matcher = Matcher();
        LatchByTiming(matcher, T0);
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(1, Message(pods: 0x58), T0));
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(2, Message(pods: 0x57), T0.AddSeconds(1)));
    }

    [Fact]
    public void Battery_far_apart_is_not_a_clue()
    {
        var matcher = Matcher();
        var message = Message();
        int pod = message.LeftBatteryPercent ?? 50;
        matcher.SetWindowsBattery(pod >= 50 ? 0 : 100, T0);
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, message, T0));
    }

    [Fact]
    public void An_absent_windows_battery_is_normal_and_still_works()
    {
        var matcher = Matcher();
        matcher.SetWindowsBattery(null, T0);
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, Message(), T0));
    }

    [Fact]
    public void A_stale_windows_battery_is_ignored()
    {
        var matcher = Matcher();
        var message = Message();
        matcher.SetWindowsBattery(message.LeftBatteryPercent, T0);
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(1, message, T0.AddMinutes(6)));
    }

    [Fact]
    public void Battery_agreement_never_picks_between_two_candidates()
    {
        var matcher = Matcher();
        var message = Message();
        matcher.SetWindowsBattery(message.LeftBatteryPercent, T0);
        matcher.Observe(1, message, T0);
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(2, Rival(), T0.AddSeconds(1)));
    }

    [Fact]
    public void A_likely_match_follows_an_address_rotation()
    {
        var matcher = Matcher();
        var message = Message();
        LatchByTiming(matcher, T0);
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(1, message, T0));

        // Old address goes quiet; a new one with the same model and battery appears.
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(2, message, T0.AddSeconds(5)));
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0.AddSeconds(5))); // retired, not a rival
    }

    [Fact]
    public void Rotation_does_not_carry_a_match_to_a_source_with_a_different_battery()
    {
        var matcher = Matcher();
        var message = Message();
        matcher.SetWindowsBattery(message.LeftBatteryPercent, T0);
        matcher.Observe(1, message, T0);

        var other = Message(pods: 0x11); // different pod battery nibbles
        Assert.NotEqual(message.LeftBatteryPercent, other.LeftBatteryPercent);
        Assert.True(Math.Abs((message.LeftBatteryPercent ?? 0) - (other.LeftBatteryPercent ?? 0)) > 10
            || Math.Abs((message.RightBatteryPercent ?? 0) - (other.RightBatteryPercent ?? 0)) > 10);
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(2, other, T0.AddSeconds(5))); // old one still in window: two candidates
    }

    [Fact]
    public void Rotation_only_carries_after_the_old_address_went_quiet()
    {
        var matcher = Matcher();
        var message = Message();
        matcher.SetWindowsBattery(message.LeftBatteryPercent, T0);
        matcher.Observe(1, message, T0);
        matcher.Observe(1, message, T0.AddSeconds(4));

        // The old address spoke 1 s ago: both are alive, so this is a second device, not a rotation.
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(2, Rival(), T0.AddSeconds(5)));
    }

    [Fact]
    public void A_match_is_forgotten_after_the_retention_limit()
    {
        var matcher = Matcher();
        var message = Message();
        LatchByTiming(matcher, T0);
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(1, message, T0));

        // A new address appears after the old record has expired: it must earn its rating again.
        matcher.SetWindowsBattery(null, T0.AddSeconds(100));
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(2, message, T0.AddSeconds(100)));
    }

    [Fact]
    public void A_quiet_source_expires_to_unknown()
    {
        var matcher = Matcher();
        matcher.Observe(1, Message(), T0);
        Assert.Equal(IdentityConfidence.Probable, matcher.ConfidenceOf(1, T0.AddSeconds(10)));
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0.AddSeconds(11)));
    }

    [Fact]
    public void A_latched_match_is_not_reported_while_a_rival_is_heard_but_returns_after()
    {
        var matcher = Matcher();
        var message = Message();
        LatchByTiming(matcher, T0);
        matcher.Observe(1, message, T0);
        matcher.Observe(2, Rival(), T0.AddSeconds(1)); // rival, not a rotation (1 is not quiet)
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0.AddSeconds(1)));

        matcher.Observe(1, message, T0.AddSeconds(15));
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.ConfidenceOf(1, T0.AddSeconds(15)));
    }

    [Fact]
    public void Changing_the_paired_model_forgets_everything()
    {
        var matcher = Matcher();
        matcher.Observe(1, Message(), T0);
        matcher.SetPairedModel(Payload.ProTwoUsbC);
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0));
    }

    [Fact]
    public void Reset_forgets_sources_but_keeps_the_windows_baseline()
    {
        var matcher = Matcher();
        matcher.ObserveWindowsConnection(true, T0);
        matcher.Observe(1, Message(), T0);
        matcher.Reset();
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, T0));
        matcher.ObserveWindowsConnection(false, T0.AddSeconds(30)); // a real transition from the kept baseline
        Assert.Equal(IdentityConfidence.LikelyYours, matcher.Observe(1, Message(), T0.AddSeconds(31)));
    }

    [Fact]
    public void Wording_never_says_confirmed_alone()
    {
        foreach (var level in Enum.GetValues<IdentityConfidence>())
            Assert.DoesNotContain("Confirmed", IdentityConfidenceText.For(level), StringComparison.Ordinal);
        Assert.Equal("Your AirPods (likely)", IdentityConfidenceText.For(IdentityConfidence.Probable));
        Assert.Equal("Your AirPods (likely)", IdentityConfidenceText.For(IdentityConfidence.LikelyYours));
        Assert.Contains("unconfirmed", IdentityConfidenceText.For(IdentityConfidence.Unknown), StringComparison.Ordinal);
    }

    [Fact]
    public void Pro_3_is_recognised_in_the_catalog()
    {
        Assert.Equal(ModelRecognition.Recognised, AppleModelCatalog.Recognise(ProThree));
        Assert.Equal("AirPods Pro 3", AppleModelCatalog.NameOf(ProThree));
    }

    [Theory]
    [InlineData(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\7&1A2B3C&0&A1B2C3D4E5F6_C00000000", (ushort)0x2720)]
    [InlineData(@"bthenum\dev_vid&0001004c_pid&2027\x", (ushort)0x2720)]
    [InlineData("VID&0001004C_PID&200E", (ushort)0x0E20)]
    public void The_model_id_is_parsed_from_apple_device_ids(string id, ushort expected)
        => Assert.Equal(expected, AppleBluetoothIdentity.TryParseModelId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bluetooth#Bluetooth00:11:22:33:44:55-aa:bb:cc:dd:ee:ff")]
    [InlineData(@"BTHENUM\VID&00010002_PID&2027\x")] // another vendor
    [InlineData(@"BTHENUM\VID&0001004C_PID&20\x")]   // too short
    [InlineData(@"BTHENUM\VID&0001004C_PID&20277\x")] // too long
    public void Non_apple_or_malformed_ids_have_no_model(string? id)
        => Assert.Null(AppleBluetoothIdentity.TryParseModelId(id));

    [Fact]
    public void The_connection_device_keeps_its_old_constructor_shape()
    {
        var device = new ConnectionDevice("id", "name", "container", true, true, false, false, null);
        Assert.Null(device.ModelId);
        Assert.Null(device.WindowsBatteryPercent);
    }

    [Fact]
    public void A_flood_of_addresses_is_bounded_and_never_rates_anyone_likely()
    {
        var matcher = Matcher();
        matcher.ObserveWindowsConnection(false, T0.AddSeconds(-30));
        matcher.ObserveWindowsConnection(true, T0); // a connect that would line up with a newly appearing source
        matcher.Observe(1, Message(), T0);
        for (ulong a = 2; a < 1000; a++) matcher.Observe(a, Message(), T0.AddMilliseconds((long)a));

        Assert.True(matcher.TrackedCount <= matcher.Options.MaxTracked);
        var at = T0.AddSeconds(2);
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(1, at));
        Assert.Equal(IdentityConfidence.Unknown, matcher.ConfidenceOf(5000, at));
        Assert.Equal(IdentityConfidence.Unknown, matcher.Observe(5000, Message(), at));
    }

    [Fact]
    public void After_the_flood_goes_quiet_a_single_source_can_be_rated_again()
    {
        var matcher = Matcher();
        for (ulong a = 1; a < 300; a++) matcher.Observe(a, Message(), T0);
        var later = T0.AddMinutes(3);
        Assert.Equal(IdentityConfidence.Probable, matcher.Observe(7, Message(), later));
        Assert.True(matcher.TrackedCount <= matcher.Options.MaxTracked);
    }
}
