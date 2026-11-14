using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>
/// One bud in an ear (or out) and the other in the case: each bud broadcasts from its own address and the
/// bud that left the case holds a stale lid counter, so the two disagree on it although they are one pair.
/// </summary>
public sealed class SplitBudsTests : IAsyncDisposable
{
    // Left bud in an ear (out of the case, one pod in the case, left primary, left in-ear bit): L80 R60, case 50, lid 1.
    private static byte[] LeftInEar(ushort model = Payload.ProThree, byte lid = 1, byte pods = 0x68, byte flagsAndCase = 0x05)
        => Payload.Build(model, status: 0x32, pods: pods, flagsAndCase: flagsAndCase, lid: lid);

    // Right bud in the case (right primary, so left/right nibbles are flipped; one pod in case): the same L80 R60, case 50, lid 2.
    private static byte[] RightInCase(ushort model = Payload.ProThree, byte lid = 2, byte pods = 0x86, byte flagsAndCase = 0x05)
        => Payload.Build(model, status: 0x52, pods: pods, flagsAndCase: flagsAndCase, lid: lid);

    private static PodReading Read(byte[] payload) => PodReading.From(ProximityMessageParser.Parse(payload)!);

    private const int Tol = 10;

    [Theory]
    [InlineData((ushort)0x2720)] // AirPods Pro 3
    [InlineData((ushort)0x1420)] // AirPods Pro 2
    [InlineData((ushort)0x0220)] // AirPods 1
    [InlineData((ushort)0x1320)] // AirPods 3: no model special-casing
    public void A_split_pair_with_different_lid_counters_is_one_pair(ushort model)
    {
        var inEar = Read(LeftInEar(model));
        var inCase = Read(RightInCase(model));
        Assert.False(inEar.InCase);
        Assert.True(inCase.InCase);
        Assert.Equal((byte)1, inEar.LidCounter);
        Assert.Equal((byte)2, inCase.LidCounter);
        Assert.True(BroadcastSiblings.AreSiblings(inEar, inCase, Tol));
        Assert.True(BroadcastSiblings.AreSiblings(inCase, inEar, Tol));
        Assert.Null(BroadcastSiblings.Disagreement(inEar, inCase, Tol));
        Assert.Equal(1, BroadcastSiblings.CountGroups([inEar, inCase], Tol));
    }

    [Fact]
    public void Both_buds_out_of_the_case_with_different_counters_are_one_pair()
    {
        // One bud in an ear, the other put back and taken out again (counter moved on): neither is in the case.
        var a = new PodReading(Payload.ProThree, 80, 60, 50, 1, InCase: false);
        var b = new PodReading(Payload.ProThree, 80, 60, 50, 2, InCase: false);
        Assert.True(BroadcastSiblings.AreSiblings(a, b, Tol));
        Assert.Null(BroadcastSiblings.Disagreement(a, b, Tol));
    }

    [Fact]
    public void A_split_pair_with_one_unknown_battery_is_still_one_pair()
    {
        var inEar = new PodReading(Payload.ProThree, 80, null, 50, 1, InCase: false);
        var inCase = new PodReading(Payload.ProThree, null, 80, 50, 2, InCase: true);
        Assert.True(BroadcastSiblings.AreSiblings(inEar, inCase, Tol));
    }

    [Fact]
    public void Two_different_pairs_with_different_lid_counters_in_the_same_state_stay_separate()
    {
        // Both buds in their cases: their counters are comparable and differ.
        var a = new PodReading(Payload.ProThree, 80, 60, 50, 1, InCase: true);
        var b = new PodReading(Payload.ProThree, 80, 60, 50, 2, InCase: true);
        Assert.False(BroadcastSiblings.AreSiblings(a, b, Tol));
        Assert.Equal("lid counter 1 vs 2", BroadcastSiblings.Disagreement(a, b, Tol));
        // And the split state never hides a real battery difference.
        var other = new PodReading(Payload.ProThree, 30, 20, 50, 2, InCase: true);
        var inEar = new PodReading(Payload.ProThree, 80, 60, 50, 1, InCase: false);
        Assert.False(BroadcastSiblings.AreSiblings(inEar, other, Tol));
        Assert.Equal(2, BroadcastSiblings.CountGroups([inEar, other], Tol));
    }

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();
    private readonly BroadcastObservationTransport _transport;

    public SplitBudsTests() => _transport = new BroadcastObservationTransport(_source, _clock);

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();

    private async Task StartAsync()
    {
        _transport.SetPairedDevice(Payload.ProThree, "paired-pro-3");
        await _transport.StartAsync();
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
    }

    private void Emit(byte[] payload, ulong address) => _source.Emit(Payload.Sighting(payload, address));

    [Fact]
    public async Task Worn_buds_heard_again_with_identical_packets_stay_out_of_the_case_past_the_artwork_freshness_window()
    {
        await StartAsync();
        // Both buds in the ears, the same payload every 2 s for 45 s: the art must never fall back to the buds in the case.
        for (int i = 0; i < 23; i++)
        {
            Emit(LeftInEar(), 1);
            Assert.True(HubViewModel.FreshBudsOut(_transport.Current, _clock.GetUtcNow()).Left, $"left bud drawn in the case at {i * 2} s");
            _clock.Advance(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Worn_buds_no_longer_heard_fall_back_to_the_neutral_art_once_the_placement_is_stale()
    {
        await StartAsync();
        Emit(LeftInEar(), 1);
        Assert.True(HubViewModel.FreshBudsOut(_transport.Current, _clock.GetUtcNow()).Left);
        _clock.Advance(HubViewModel.ArtworkEarFreshness + TimeSpan.FromSeconds(1));
        Assert.False(HubViewModel.FreshBudsOut(_transport.Current, _clock.GetUtcNow()).Any);
    }

    [Fact]
    public async Task One_bud_in_an_ear_and_one_in_the_case_is_accepted_with_batteries_and_the_left_bud_out()
    {
        await StartAsync();
        Emit(LeftInEar(), 1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(RightInCase(), 2);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(LeftInEar(), 1);

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.Equal(AttributionVerdict.Accepted, _transport.LastDecision!.Verdict);
        var state = _transport.Current!;
        Assert.Equal(80, state.Battery.Left.Level.Value);
        Assert.Equal(60, state.Battery.Right.Level.Value);
        Assert.Equal(50, state.Battery.Case.Level.Value);
        Assert.Equal(PodPlacement.InEar, state.Ears.Left.Value);
        var budsOut = HubViewModel.FreshBudsOut(state, _clock.GetUtcNow());
        Assert.True(budsOut.Left);
        Assert.False(budsOut.Right);
        Assert.Single(_transport.Sources); // one pair in the list, not two
    }

    [Fact]
    public async Task The_in_ear_buds_own_message_wins_the_ear_state_when_the_case_bud_is_heard_last()
    {
        await StartAsync();
        Emit(LeftInEar(), 1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(RightInCase(), 2);

        var state = _transport.Current!;
        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.Equal(PodPlacement.InEar, state.Ears.Left.Value);
        Assert.True(HubViewModel.FreshBudsOut(state, _clock.GetUtcNow()).Left);
    }

    [Fact]
    public async Task A_battery_one_bud_did_not_report_is_filled_in_from_its_sibling()
    {
        await StartAsync();
        Emit(RightInCase(), 2);                       // L80 R60
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(LeftInEar(pods: 0xF8), 1);               // in-ear bud only knows its own level: L80, R unknown

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.Equal(80, _transport.Current!.Battery.Left.Level.Value);
        Assert.Equal(60, _transport.Current.Battery.Right.Level.Value);
    }

    [Fact]
    public async Task Two_different_pairs_with_different_batteries_are_still_ambiguous()
    {
        await StartAsync();
        Emit(LeftInEar(), 1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(RightInCase(pods: 0x32), 2); // a stranger: L20 R30

        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);
    }
}
