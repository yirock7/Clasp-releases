using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

/// <summary>
/// Payloads captured from the owner's AirPods Pro 3 (not synthetic), driven through the real parser,
/// policy, matcher and transport with a paired device id that carries the Windows form of the model.
/// </summary>
public sealed class RealCaptureTests : IAsyncDisposable
{
    private const string PairedWindowsId = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\7&1A2B3C&0&A1B2C3D4E5F6_C00000000";

    // Case open, in hardware: the two buds broadcast from two addresses (L100 R100 C70, lid counter 1).
    private const string CaseOpenBudA = "071901272055AAB731000432C62B3E8B2D3A32453E658DEE6CB9CD";
    private const string CaseOpenBudB = "071901272035AAB7310004210CAF218864AF17C899A41BE5DF5800";

    // In ears, connected, playing audio: case level unknown (nibble F), lid counter 0.
    private const string InEarA = "07190127200BAA8F10000572F78678F181E28A5F08CA3443DEDA3C";
    private const string InEarB = "07190127200BAA8F100005C24B47A0BE70E840337B2292DC0EEA78";

    // Unrelated short encrypted variant (length 0x11, flag byte 0x06).
    private const string ShortVariantA = "071106133C5D7E91A3CA8A79EBACD5F7296577";
    private const string ShortVariantB = "071106567F2DB4F2C6EC3F790D75FC646E8372";

    private const ulong BudA = 0x5621843AC971, BudB = 0x4FAF91585DB2, InEarAddress = 0x1234567890AB, Stranger = 0xE34645790EBE;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();
    private readonly BroadcastObservationTransport _transport;

    public RealCaptureTests() => _transport = new BroadcastObservationTransport(_source, _clock);

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();

    private async Task StartPairedAsync()
    {
        _transport.SetPairedDevice(AppleBluetoothIdentity.TryParseModelId(PairedWindowsId), "paired-pro-3");
        await _transport.StartAsync();
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
    }

    private void Emit(string hex, ulong address) => _source.Emit(Payload.Sighting(Convert.FromHexString(hex), address));

    [Fact]
    public void The_windows_pid_converts_to_the_wire_model_id_the_catalog_lists()
    {
        Assert.Equal((ushort?)0x2720, AppleBluetoothIdentity.TryParseModelId(PairedWindowsId));
        Assert.Equal((ushort)0x2720, ProximityMessageParser.Parse(Convert.FromHexString(CaseOpenBudA))!.ModelId);
        Assert.Equal("AirPods Pro 3", AppleModelCatalog.NameOf(0x2720));
    }

    [Fact]
    public async Task Both_buds_of_a_case_open_pair_show_one_fresh_reading_not_ambiguous()
    {
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(CaseOpenBudB, BudB);

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.NotEqual(AttributionVerdict.Ambiguous, _transport.LastDecision!.Verdict);
        Assert.True(_transport.IdentityConfidence >= IdentityConfidence.Probable);
        var battery = _transport.Current!.Battery;
        Assert.Equal(100, battery.Left.Level.Value);
        Assert.Equal(100, battery.Right.Level.Value);
        Assert.Equal(70, battery.Case.Level.Value);
        Assert.Equal("Your AirPods (likely)", _transport.Current.DisplayName);
        Assert.Equal("AirPods Pro 3", _transport.Current.ModelName);

        // One device in the list, with one stable id while the buds alternate.
        var first = Assert.Single(_transport.Sources);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(CaseOpenBudA, BudA);
        var again = Assert.Single(_transport.Sources);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Number, again.Number);
        Assert.True(again.ThisPodInCase); // the bud that is in the case is not blanked by the other bud
        Assert.False(again.LidClosed);   // null from the other bud does not wipe the known lid state
    }

    [Fact]
    public async Task A_battery_step_between_the_buds_still_counts_as_one_pair()
    {
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        Emit(CaseOpenBudB.Replace("AAB731", "AAB631", StringComparison.Ordinal), BudB); // C=60 on one bud only

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
    }

    [Fact]
    public async Task In_ear_levels_are_live_and_the_case_keeps_its_last_value_with_its_age()
    {
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        var caseHeardAt = _clock.GetUtcNow();

        // The case closes; a few seconds later the buds are in the ears on a new address (lid counter differs, case unknown).
        _clock.Advance(TimeSpan.FromSeconds(3));
        Emit(InEarA, InEarAddress);

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        var battery = _transport.Current!.Battery;
        Assert.Equal(100, battery.Left.Level.Value);
        Assert.Equal(100, battery.Right.Level.Value);
        Assert.Equal(_clock.GetUtcNow(), battery.Left.Level.ObservedAt);
        Assert.Equal(70, battery.Case.Level.Value);
        Assert.Equal(caseHeardAt, battery.Case.Level.ObservedAt); // old, not claimed as live

        // The second in-ear message from the same address does not blank the case either.
        _clock.Advance(TimeSpan.FromSeconds(1));
        Emit(InEarB, InEarAddress);
        Assert.Equal(70, _transport.Current!.Battery.Case.Level.Value);
    }

    [Fact]
    public async Task In_ear_on_a_new_address_after_the_old_one_went_quiet_is_not_ambiguous()
    {
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        Emit(CaseOpenBudB, BudB);
        _clock.Advance(TimeSpan.FromSeconds(12));
        Emit(InEarA, InEarAddress);

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.Equal(100, _transport.Current!.Battery.Left.Level.Value);
        Assert.Equal(70, _transport.Current.Battery.Case.Level.Value);
    }

    [Fact]
    public async Task A_different_pair_heard_at_the_same_time_with_clearly_different_batteries_is_still_ambiguous()
    {
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, pods: 0x33, flagsAndCase: 0x45, lid: 0x0B), 0xCAFE));

        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);
        Assert.Equal(AttributionVerdict.Ambiguous, _transport.LastDecision!.Verdict);
        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
        Assert.False(_transport.Current!.Battery.Left.Level.HasValue); // nothing is picked
    }

    [Fact]
    public async Task A_second_pair_with_a_different_lid_counter_and_case_is_still_ambiguous()
    {
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        // Same bud levels but the case level and lid counter disagree beyond tolerance: another pair.
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, pods: 0xAA, flagsAndCase: 0x4A, lid: 0x03), 0xCAFE));

        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);
    }

    [Fact]
    public void The_short_encrypted_variant_is_not_a_proximity_message()
    {
        Assert.Null(ProximityMessageParser.Parse(Convert.FromHexString(ShortVariantA)));
        Assert.Null(ProximityMessageParser.Parse(Convert.FromHexString(ShortVariantB)));
        Assert.Null(ProximityMessageParser.Parse([0x07, 0x11, 0x02, 0x24, 0x20, 0x22, 0x58, 0x47, 0x0B]));
        Assert.NotNull(ProximityMessageParser.Parse(Convert.FromHexString(CaseOpenBudA)));
    }

    [Fact]
    public async Task The_short_variant_never_becomes_a_candidate_beside_the_real_pair()
    {
        await StartPairedAsync();
        Emit(ShortVariantA, Stranger);
        Emit(CaseOpenBudA, BudA);
        Emit(ShortVariantB, Stranger);

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.Single(_transport.Sources);
    }

    [Fact]
    public async Task Without_a_paired_device_the_short_variant_is_ignored()
    {
        await _transport.StartAsync();
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        Emit(ShortVariantA, Stranger);

        Assert.Equal(BroadcastPhase.Waiting, _transport.Phase);
        Assert.Empty(_transport.Sources);
    }

    [Fact]
    public async Task The_last_reading_stays_with_its_age_after_the_buds_go_quiet_until_observing_stops()
    {
        var published = new List<DeviceState>();
        _transport.StateChanged += (_, state) => published.Add(state);
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        var heardAt = _clock.GetUtcNow();

        _clock.Advance(TimeSpan.FromMinutes(12));
        Assert.Equal(BroadcastPhase.Lost, _transport.Phase);
        var state = _transport.Current!;
        Assert.Equal(100, state.Battery.Left.Level.Value);
        Assert.Equal(70, state.Battery.Case.Level.Value);
        Assert.Equal(heardAt, state.Battery.Left.Level.ObservedAt); // the real age, never refreshed
        Assert.False(state.Link.Nearby.Value);                     // and not claimed to be live

        // The age is republished while the reading is kept, so "12 min ago" can keep up.
        int before = published.Count;
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(published.Count > before);
        Assert.Equal(heardAt, _transport.Current!.Battery.Left.Level.ObservedAt);

        await _transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        Assert.False(_transport.Current!.Battery.Left.Level.HasValue);
    }

    [Fact]
    public async Task Changing_the_selected_device_drops_the_kept_reading_and_the_kept_case()
    {
        await StartPairedAsync();
        Emit(CaseOpenBudA, BudA);
        _transport.SetPairedDevice(AppleBluetoothIdentity.TryParseModelId(PairedWindowsId), "another-pair");
        Assert.False(_transport.Current!.Battery.Left.Level.HasValue);

        Emit(InEarA, InEarAddress);
        Assert.Equal(100, _transport.Current!.Battery.Left.Level.Value);
        Assert.False(_transport.Current.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task A_refused_model_is_logged_once_per_change_not_per_broadcast()
    {
        var notes = new List<string>();
        _transport.Diagnostic += (_, note) => notes.Add(note);
        _transport.SetPairedDevice(0x0E20, "paired-pro-1");
        await _transport.StartAsync();
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        notes.Clear();

        for (int i = 0; i < 5; i++) Emit(CaseOpenBudA, BudA);

        var note = Assert.Single(notes);
        Assert.Contains("0x2720", note, StringComparison.Ordinal);
        Assert.DoesNotContain("5621843AC971", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Over_hours_of_wear_the_address_rotates_the_battery_falls_and_the_display_stays_fresh_and_newest()
    {
        await StartPairedAsync();
        int hours = 6;
        AdvertisementSighting? previous = null;
        for (int hour = 0; hour < hours; hour++)
        {
            ulong address = 0x2000UL + (ulong)hour;
            // Battery falls one 10 % step an hour; right is one step below left.
            byte pods = (byte)(((10 - hour) << 4) | (9 - hour));
            var payload = Payload.Sighting(Payload.Build(model: Payload.ProThree, status: 0x0B, pods: pods, flagsAndCase: 0x8F, lid: 0x00), address);
            var expected = ProximityMessageParser.Parse(payload.ManufacturerData.ToArray())!;

            if (hour % 2 == 0)
            {
                // Rotation with an overlap: the old address is heard, then the new one 2 s later (both inside the window).
                if (previous is not null) _source.Emit(previous);
                _clock.Advance(TimeSpan.FromSeconds(2));
            }
            else
            {
                // Rotation with a gap: the old address has been quiet for longer than the candidate window.
                _clock.Advance(TimeSpan.FromSeconds(40));
            }
            _source.Emit(payload);

            Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
            Assert.NotEqual(AttributionVerdict.Ambiguous, _transport.LastDecision!.Verdict);
            Assert.True(_transport.IdentityConfidence >= IdentityConfidence.Probable);
            Assert.Equal(expected.LeftBatteryPercent, _transport.Current!.Battery.Left.Level.Value);
            Assert.Equal(expected.RightBatteryPercent, _transport.Current.Battery.Right.Level.Value);
            Assert.Equal(_clock.GetUtcNow(), _transport.Current.Battery.Left.Level.ObservedAt);
            Assert.Single(_transport.Sources); // one device, never two while the address rotates

            // The rest of the hour: sparse broadcasts from the current address, one per half minute, display never leaves Fresh.
            for (int minute = 0; minute < 60; minute++)
            {
                _clock.Advance(TimeSpan.FromSeconds(30));
                _source.Emit(payload);
                _clock.Advance(TimeSpan.FromSeconds(30));
                Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
            }
            previous = payload;
        }
        Assert.Equal(50, _transport.Current!.Battery.Left.Level.Value);
    }

    [Fact]
    public async Task A_steady_stream_of_identical_packets_keeps_the_shown_age_current()
    {
        await StartPairedAsync();
        for (int i = 0; i < 60; i++) // 4 minutes of the very same payload every 4 s
        {
            Emit(InEarA, InEarAddress);
            _clock.Advance(TimeSpan.FromSeconds(4));
        }

        var observedAt = _transport.Current!.Battery.Left.Level.ObservedAt!.Value;
        Assert.True(_clock.GetUtcNow() - observedAt <= TimeSpan.FromSeconds(70), "the panel would show a live reading as minutes old");
    }

    [Fact]
    public async Task A_quiet_gap_in_the_ears_keeps_the_identity_rating_until_another_source_is_heard()
    {
        await StartPairedAsync();
        Emit(InEarA, InEarAddress);
        Assert.True(_transport.IdentityConfidence >= IdentityConfidence.Probable);

        _clock.Advance(TimeSpan.FromSeconds(60)); // far beyond the 10 s candidate window, still fresh
        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.True(_transport.IdentityConfidence >= IdentityConfidence.Probable);

        _clock.Advance(TimeSpan.FromMinutes(3)); // stale: values and rating stay, ears are dropped
        Assert.Equal(BroadcastPhase.Stale, _transport.Phase);
        Assert.True(_transport.IdentityConfidence >= IdentityConfidence.Probable);
        Assert.False(_transport.Current!.Ears.Primary.HasValue);

        // A rival pair with clearly different batteries still makes it ambiguous.
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, status: 0x0B, pods: 0x33, flagsAndCase: 0x8F, lid: 0x00), 0xCAFE));
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, status: 0x0B, pods: 0xAA, flagsAndCase: 0x8F, lid: 0x00), InEarAddress));
        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);
        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
    }

    [Fact]
    public async Task Unequal_levels_with_the_sides_swapped_between_the_buds_are_still_one_pair()
    {
        await StartPairedAsync();
        // L100/R50 from one bud and L50/R100 from the other (as if the primary flip were wrong).
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, status: 0x2B, pods: 0xA5, flagsAndCase: 0x8F, lid: 0x00), 0x11));
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, status: 0x0B, pods: 0xA5, flagsAndCase: 0x8F, lid: 0x00), 0x22));

        Assert.Equal(BroadcastPhase.Fresh, _transport.Phase);
        Assert.NotEqual(AttributionVerdict.Ambiguous, _transport.LastDecision!.Verdict);
        Assert.True(_transport.Current!.Battery.Left.Level.HasValue);
    }
}
