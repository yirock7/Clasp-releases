using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>Ingress token buckets and the once-per-generation grouping cache. No radio is touched.</summary>
public sealed class BleIngressLimitTests
{
    private const ushort Model = Payload.ProThree;
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static ProximityMessage Message(byte status) => ProximityMessageParser.Parse(Payload.Build(model: Model, status: status))!;

    [Fact]
    public void Grouping_work_is_per_observation_not_per_observation_times_sources()
    {
        var matcher = new PairedIdentityMatcher();
        matcher.SetPairedModel(Model);
        var addresses = Enumerable.Range(1, 64).Select(i => (ulong)(0x100000 + i)).ToArray();
        var now = T0;
        const int sightings = 10_000;
        for (int i = 0; i < sightings; i++)
        {
            now += TimeSpan.FromMilliseconds(1);
            // Alternating valid raw status keeps every address sibling-compatible but never an exact duplicate.
            matcher.Observe(addresses[i % addresses.Length], Message((byte)(i % 2 == 0 ? 0x22 : 0x23)), now);
            foreach (var address in addresses) matcher.ConfidenceOf(address, now); // what the transport does per displayed source
        }

        Assert.True(matcher.GroupingComputations <= sightings + 1, $"grouped {matcher.GroupingComputations} times for {sightings} sightings");
    }

    [Fact]
    public void The_cached_group_count_gives_the_same_ratings_as_a_fresh_matcher()
    {
        var cached = new PairedIdentityMatcher();
        cached.SetPairedModel(Model);
        var now = T0;
        cached.Observe(1, Message(0x22), now);
        Assert.Equal(IdentityConfidence.Probable, cached.ConfidenceOf(1, now));
        // A second, different pair must flip the cached answer to Unknown straight away.
        var other = ProximityMessageParser.Parse(Payload.Build(model: Model, pods: 0x13, flagsAndCase: 0x47, lid: 0x0C))!;
        cached.Observe(2, other, now);
        Assert.Equal(IdentityConfidence.Unknown, cached.ConfidenceOf(1, now));
        Assert.Equal(IdentityConfidence.Unknown, cached.ConfidenceOf(2, now));
        // Once the first one ages out of the candidate window, the cache must not keep it counted.
        now += TimeSpan.FromSeconds(11);
        cached.Observe(2, other, now);
        Assert.Equal(IdentityConfidence.Probable, cached.ConfidenceOf(2, now));
    }

    private static async Task<(BroadcastObservationTransport Transport, FakeAdvertisementSource Source, FakeTimeProvider Clock)> StartAsync(BroadcastObservationOptions options)
    {
        var source = new FakeAdvertisementSource();
        var clock = new FakeTimeProvider(T0);
        var transport = new BroadcastObservationTransport(source, clock, options);
        await transport.StartAsync();
        await transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        return (transport, source, clock);
    }

    [Fact]
    public async Task One_address_past_its_burst_is_dropped_and_counted()
    {
        var (transport, source, clock) = await StartAsync(new BroadcastObservationOptions { IngressPerAddressBurst = 10 });
        await using var _ = transport;
        for (int i = 0; i < 30; i++) source.Emit(Payload.Sighting(Payload.Build(model: Model)));

        Assert.Equal(20, transport.RateLimitedSightings);

        clock.Advance(TimeSpan.FromSeconds(1)); // refills 5 tokens at the default 5/s
        for (int i = 0; i < 10; i++) source.Emit(Payload.Sighting(Payload.Build(model: Model)));
        Assert.Equal(25, transport.RateLimitedSightings);
    }

    [Fact]
    public async Task The_global_bucket_limits_many_addresses_and_dropped_sightings_change_no_state()
    {
        var (transport, source, _) = await StartAsync(new BroadcastObservationOptions { IngressGlobalBurst = 5 });
        await using var _ = transport;
        for (int i = 0; i < 20; i++)
            source.Emit(Payload.Sighting(Payload.Build(model: Model), address: (ulong)(0x200 + i)));

        Assert.Equal(15, transport.RateLimitedSightings);
        Assert.True(transport.Sources.Count <= 5);
    }

    [Fact]
    public void Non_positive_ingress_settings_are_refused()
    {
        var source = new FakeAdvertisementSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastObservationTransport(source, null, new BroadcastObservationOptions { IngressGlobalBurst = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastObservationTransport(source, null, new BroadcastObservationOptions { IngressPerAddressRatePerSecond = 0 }));
    }
}
