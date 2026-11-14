using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Tests.Ble;

public class BroadcastAttributionPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static AttributionDecision Evaluate(BroadcastAttributionPolicy policy, AdvertisementSighting s, DateTimeOffset at, bool optedIn = true)
        => policy.Evaluate(s, ProximityMessageParser.Parse(s.CompanyId, s.ManufacturerData.ToArray()), optedIn, at);

    [Fact]
    public void Nothing_is_accepted_before_the_user_chooses_to_observe()
    {
        var d = Evaluate(new BroadcastAttributionPolicy(), Payload.Sighting(Payload.Build()), T0, optedIn: false);
        Assert.Equal(AttributionVerdict.Rejected, d.Verdict);
    }

    [Theory]
    [InlineData(ProximityMessage.AppleCompanyId)]
    [InlineData((ushort)0x0006)]
    public void A_name_alone_never_identifies_a_device(ushort company)
    {
        // Apple-looking name, but the payload is not a proximity-pairing message.
        var s = Payload.Sighting([0x10, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05], name: "AirPods Pro", company: company);
        var d = Evaluate(new BroadcastAttributionPolicy(), s, T0);

        Assert.Equal(AttributionVerdict.Rejected, d.Verdict);
        Assert.Contains("name", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Other_company_with_a_valid_looking_body_is_rejected()
    {
        var d = Evaluate(new BroadcastAttributionPolicy(), Payload.Sighting(Payload.Build(), company: 0x0006), T0);
        Assert.Equal(AttributionVerdict.Rejected, d.Verdict);
    }

    [Fact]
    public void Listed_model_is_accepted_with_its_name_but_identity_unconfirmed()
    {
        var d = Evaluate(new BroadcastAttributionPolicy(), Payload.Sighting(Payload.Build()), T0);

        Assert.Equal(AttributionVerdict.Accepted, d.Verdict);
        Assert.Equal(ModelRecognition.Recognised, d.Recognition);
        Assert.Equal("AirPods Pro 2 (USB-C)", d.ModelName);
        Assert.False(d.IdentityConfirmed);
    }

    [Fact]
    public void Unlisted_model_id_is_unrecognised_family_and_claims_no_model_name()
    {
        var d = Evaluate(new BroadcastAttributionPolicy(), Payload.Sighting(Payload.Build(model: Payload.Unlisted)), T0);

        Assert.Equal(AttributionVerdict.Accepted, d.Verdict);
        Assert.Equal(ModelRecognition.Unrecognised, d.Recognition);
        Assert.Null(d.ModelName);
        Assert.False(d.IdentityConfirmed);
        Assert.DoesNotContain("Pro 3", AppleModelCatalog.DisplayNameOf(Payload.Unlisted), StringComparison.Ordinal);
        Assert.StartsWith(AppleModelCatalog.UnrecognisedName, AppleModelCatalog.DisplayNameOf(Payload.Unlisted), StringComparison.Ordinal);
    }

    [Fact]
    public void Unlisted_model_id_can_be_refused_by_option()
    {
        var policy = new BroadcastAttributionPolicy(new BroadcastAttributionOptions { AcceptUnlistedModelIds = false });
        Assert.Equal(AttributionVerdict.Rejected, Evaluate(policy, Payload.Sighting(Payload.Build(model: Payload.Unlisted)), T0).Verdict);
    }

    [Fact]
    public void A_pinned_model_id_filters_other_models_without_counting_them()
    {
        var policy = new BroadcastAttributionPolicy(new BroadcastAttributionOptions { ExpectedModelId = Payload.Unlisted });
        Assert.Equal(AttributionVerdict.Rejected, Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0).Verdict);
        Assert.Equal(AttributionVerdict.Accepted, Evaluate(policy, Payload.Sighting(Payload.Build(model: Payload.Unlisted), address: 2), T0).Verdict);
    }

    [Fact]
    public void Two_addresses_within_the_window_are_ambiguous_not_a_pick()
    {
        var policy = new BroadcastAttributionPolicy();
        Assert.Equal(AttributionVerdict.Accepted, Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0).Verdict);

        // A second pair: clearly different pod batteries, so it is not the other bud of the first.
        var d = Evaluate(policy, Payload.Sighting(Payload.Build(pods: 0x11), address: 2), T0.AddSeconds(3));
        Assert.Equal(AttributionVerdict.Ambiguous, d.Verdict);
        Assert.Equal(2, d.CandidateCount);

        // Hearing the first one again does not resolve it while both are inside the window.
        Assert.Equal(AttributionVerdict.Ambiguous, Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0.AddSeconds(4)).Verdict);
    }

    [Fact]
    public void A_partial_reading_does_not_bridge_two_conflicting_pairs_into_one_candidate()
    {
        var policy = new BroadcastAttributionPolicy();
        // Pods 100/100 throughout. Case 70 + lid 1, then no case level + lid 0 (compatible with both), then case 30 + lid 2.
        Assert.Equal(AttributionVerdict.Accepted, Evaluate(policy, Payload.Sighting(Payload.Build(pods: 0xAA, flagsAndCase: 0x47, lid: 1), address: 1), T0).Verdict);
        Assert.Equal(AttributionVerdict.Accepted, Evaluate(policy, Payload.Sighting(Payload.Build(pods: 0xAA, flagsAndCase: 0x4F, lid: 0), address: 2), T0.AddSeconds(1)).Verdict);

        var d = Evaluate(policy, Payload.Sighting(Payload.Build(pods: 0xAA, flagsAndCase: 0x43, lid: 2), address: 3), T0.AddSeconds(2));
        Assert.Equal(AttributionVerdict.Ambiguous, d.Verdict);
    }

    [Fact]
    public void Same_address_with_differing_model_ids_is_ambiguous()
    {
        var policy = new BroadcastAttributionPolicy();
        Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0);
        var d = Evaluate(policy, Payload.Sighting(Payload.Build(model: Payload.Unlisted), address: 1), T0.AddSeconds(1));
        Assert.Equal(AttributionVerdict.Ambiguous, d.Verdict);
    }

    [Fact]
    public void Rotated_random_address_stops_counting_after_the_window()
    {
        var policy = new BroadcastAttributionPolicy(); // 10 s window
        Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0);

        // The old address has gone quiet for longer than the window: no false duplicate.
        var d = Evaluate(policy, Payload.Sighting(Payload.Build(), address: 2), T0.AddSeconds(11));
        Assert.Equal(AttributionVerdict.Accepted, d.Verdict);
        Assert.Equal(1, d.CandidateCount);
    }

    [Fact]
    public void Ambiguity_from_a_rotation_overlap_is_time_bounded()
    {
        var policy = new BroadcastAttributionPolicy();
        Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0);
        Assert.Equal(AttributionVerdict.Ambiguous, Evaluate(policy, Payload.Sighting(Payload.Build(pods: 0x11), address: 2), T0.AddSeconds(5)).Verdict);

        // Only the new address keeps talking; once the old one is outside the window, it resolves.
        Assert.Equal(AttributionVerdict.Accepted, Evaluate(policy, Payload.Sighting(Payload.Build(pods: 0x11), address: 2), T0.AddSeconds(16)).Verdict);
        Assert.Equal(1, policy.CandidateCount(T0.AddSeconds(16)));
        Assert.Equal(0, policy.CandidateCount(T0.AddSeconds(30)));
    }

    [Fact]
    public void A_flood_of_look_alike_addresses_stays_bounded_and_turns_ambiguous()
    {
        var policy = new BroadcastAttributionPolicy();
        int max = policy.Options.MaxCandidates;
        // Identical readings would all group as one pair, so only the cap can stop a spoofed "unique" source.
        Assert.Equal(AttributionVerdict.Accepted, Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0).Verdict);
        for (ulong a = 2; a <= (ulong)max + 200; a++)
            Evaluate(policy, Payload.Sighting(Payload.Build(), address: a), T0.AddMilliseconds((long)a));

        var late = Evaluate(policy, Payload.Sighting(Payload.Build(), address: 1), T0.AddSeconds(1));
        Assert.Equal(AttributionVerdict.Ambiguous, late.Verdict);
        Assert.True(policy.CandidateCount(T0.AddSeconds(1)) > 1);
        Assert.Equal(AttributionVerdict.Ambiguous, Evaluate(policy, Payload.Sighting(Payload.Build(), address: 99999), T0.AddSeconds(2)).Verdict);
    }

    [Fact]
    public void The_candidate_table_never_grows_past_its_cap_and_recovers_once_the_flood_goes_quiet()
    {
        var policy = new BroadcastAttributionPolicy();
        for (ulong a = 1; a <= 500; a++) Evaluate(policy, Payload.Sighting(Payload.Build(), address: a), T0);
        Assert.True(policy.CandidateCount(T0) > 1);

        var later = T0.AddSeconds(30);
        Assert.Equal(AttributionVerdict.Accepted, Evaluate(policy, Payload.Sighting(Payload.Build(), address: 7), later).Verdict);
    }
}
