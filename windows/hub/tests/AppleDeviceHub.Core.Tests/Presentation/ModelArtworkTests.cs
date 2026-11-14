using AppleDeviceHub.Core.AirPods;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Tests.Ble;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Presentation;

/// <summary>Each listed model maps to one artwork group, and the state and view model carry it.</summary>
public sealed class ModelArtworkTests
{
    private const ushort StudioPro = 0x1720;
    private const ushort SoloBuds = 0x2620;
    private static readonly object Gate = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeAdvertisementSource _source = new();

    private static void Dispatch(Action action)
    {
        lock (Gate) action();
    }

    private static readonly (ushort Id, ArtworkGroup Group)[] Planned =
    [
        (0x0220, ArtworkGroup.StemEarbuds), (0x0F20, ArtworkGroup.StemEarbuds), (0x1320, ArtworkGroup.StemEarbuds),
        (0x1920, ArtworkGroup.StemEarbuds), (0x1B20, ArtworkGroup.StemEarbuds), (0x3620, ArtworkGroup.StemEarbuds),
        (0x3020, ArtworkGroup.StemEarbuds),
        (0x0E20, ArtworkGroup.ProEarbuds), (0x1420, ArtworkGroup.ProEarbuds), (0x2420, ArtworkGroup.ProEarbuds),
        (0x2720, ArtworkGroup.ProEarbuds),
        (0x0A20, ArtworkGroup.MaxHeadphones), (0x1F20, ArtworkGroup.MaxHeadphones), (0x2D20, ArtworkGroup.MaxHeadphones),
        (0x0920, ArtworkGroup.OverEarHeadphones), (0x1720, ArtworkGroup.OverEarHeadphones),
        (0x0620, ArtworkGroup.OnEarHeadphones), (0x0C20, ArtworkGroup.OnEarHeadphones), (0x2520, ArtworkGroup.OnEarHeadphones),
        (0x1120, ArtworkGroup.BudsWithCase), (0x1620, ArtworkGroup.BudsWithCase), (0x2620, ArtworkGroup.BudsWithCase),
        (0x1220, ArtworkGroup.BudsWithCase), (0x2F20, ArtworkGroup.BudsWithCase),
        (0x0B20, ArtworkGroup.EarHookWithCase), (0x1D20, ArtworkGroup.EarHookWithCase),
        (0x0320, ArtworkGroup.EarHookCorded), (0x0D20, ArtworkGroup.EarHookCorded),
        (0x0520, ArtworkGroup.Neckband), (0x1020, ArtworkGroup.Neckband),
        (0x3820, ArtworkGroup.Generic),
    ];

    [Fact]
    public void Every_listed_id_has_the_planned_group()
    {
        Assert.Equal(31, Planned.Length);
        Assert.Equal(AppleModelCatalog.ListedIds.OrderBy(x => x), Planned.Select(p => p.Id).OrderBy(x => x));
        foreach (var (id, group) in Planned)
        {
            Assert.True(Enum.IsDefined(group));
            Assert.Equal(group, AppleModelCatalog.Find(id)!.Artwork);
            Assert.Equal(group, AppleModelCatalog.ArtworkOf(id));
        }
    }

    [Fact]
    public void Unlisted_and_missing_ids_are_generic()
    {
        Assert.Equal(ArtworkGroup.Generic, AppleModelCatalog.ArtworkOf(null));
        Assert.Equal(ArtworkGroup.Generic, AppleModelCatalog.ArtworkOf(0xFFFF));
        Assert.Equal(ArtworkGroup.Generic, AppleModelCatalog.ArtworkOf(0x0000));
    }

    [Fact]
    public void An_unknown_model_with_an_earbud_layout_gets_the_case_drawing_and_other_layouts_stay_generic()
    {
        Assert.Equal(ArtworkGroup.StemEarbuds, AppleModelCatalog.ArtworkOf(null, BatteryLayout.EarbudsWithCase));
        Assert.Equal(ArtworkGroup.StemEarbuds, AppleModelCatalog.ArtworkOf(0xFFFF, BatteryLayout.EarbudsWithCase));
        Assert.Equal(ArtworkGroup.StemEarbuds, AppleModelCatalog.ArtworkOf(0xFFFF, BatteryLayout.EarbudsNoCaseBattery));
        Assert.Equal(ArtworkGroup.Generic, AppleModelCatalog.ArtworkOf(null, BatteryLayout.SingleBattery));
        Assert.Equal(ArtworkGroup.Generic, AppleModelCatalog.ArtworkOf(0xFFFF, BatteryLayout.SingleBattery));
        // A listed id keeps its own group whatever layout is passed.
        Assert.Equal(ArtworkGroup.MaxHeadphones, AppleModelCatalog.ArtworkOf(0x0A20, BatteryLayout.EarbudsWithCase));
        Assert.True(AppleModelCatalog.ArtworkOf(null, BatteryLayout.EarbudsWithCase).HasCase());
    }

    [Fact]
    public void A_group_has_a_case_exactly_when_the_layout_is_not_single_battery()
    {
        foreach (var id in AppleModelCatalog.ListedIds)
        {
            var info = AppleModelCatalog.Find(id)!;
            if (info.Artwork == ArtworkGroup.Generic) continue;
            Assert.True(info.Artwork.HasCase() == (info.Layout != BatteryLayout.SingleBattery), info.Name);
        }
    }

    [Fact]
    public void Only_beats_360_is_generic_among_listed_models()
    {
        var generic = AppleModelCatalog.ListedIds.Where(id => AppleModelCatalog.Find(id)!.Artwork == ArtworkGroup.Generic);
        Assert.Equal(new ushort[] { 0x3820 }, generic);
    }

    // ---- transport ----

    [Fact]
    public async Task The_transport_model_is_null_when_nothing_is_known_then_the_shown_model()
    {
        await using var transport = new BroadcastObservationTransport(_source, _clock);
        await transport.StartAsync();
        await transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        Assert.Null(transport.Current!.ModelId);

        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, status: 0x22, pods: 0x58, flagsAndCase: 0x47)));
        Assert.NotEqual(BroadcastPhase.Waiting, transport.Phase);
        Assert.Equal((ushort?)Payload.ProThree, transport.Current!.ModelId);
    }

    [Fact]
    public async Task The_transport_model_is_the_selected_model_while_waiting_and_follows_a_new_selection()
    {
        await using var transport = new BroadcastObservationTransport(_source, _clock);
        transport.SetPairedDevice(StudioPro, "studio");
        await transport.StartAsync();
        await transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
        Assert.Equal(BroadcastPhase.Waiting, transport.Phase);
        Assert.Equal((ushort?)StudioPro, transport.Current!.ModelId);

        transport.SetPairedDevice(Payload.ProThree, "pro3");
        Assert.Equal(BroadcastPhase.Waiting, transport.Phase);
        Assert.Equal((ushort?)Payload.ProThree, transport.Current!.ModelId);

        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, status: 0x22, pods: 0x58, flagsAndCase: 0x47)));
        Assert.NotEqual(BroadcastPhase.Waiting, transport.Phase);
        Assert.Equal((ushort?)Payload.ProThree, transport.Current!.ModelId);
    }

    // ---- view model ----

    private async Task<(HubViewModel Vm, BroadcastObservationTransport Transport)> StartAsync()
    {
        var transport = new BroadcastObservationTransport(_source, _clock);
        var vm = new HubViewModel(transport, BroadcastObservationTransport.DeviceId, Dispatch, time: _clock);
        await vm.StartAsync();
        await vm.ConnectAsync();
        return (vm, transport);
    }

    [Fact]
    public async Task The_view_model_raises_the_artwork_only_when_the_group_changes()
    {
        var (vm, transport) = await StartAsync();
        await using var _ = vm;
        // No model is known yet and the layout is earbuds with a case, so the case drawing shows, never headphones.
        Assert.Equal(ArtworkGroup.StemEarbuds, vm.ArtworkGroup);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        transport.SetPairedDevice(Payload.ProThree, "pro3");
        Assert.Equal(ArtworkGroup.ProEarbuds, vm.ArtworkGroup);
        Assert.Contains(nameof(HubViewModel.ArtworkGroup), raised);

        raised.Clear();
        transport.SetPairedDevice(0x2420, "pro2"); // another model, same group
        Assert.Equal(ArtworkGroup.ProEarbuds, vm.ArtworkGroup);
        Assert.DoesNotContain(nameof(HubViewModel.ArtworkGroup), raised);

        transport.SetPairedDevice(StudioPro, "studio");
        Assert.Equal(ArtworkGroup.OverEarHeadphones, vm.ArtworkGroup);
        Assert.Contains(nameof(HubViewModel.ArtworkGroup), raised);
    }

    [Fact]
    public async Task The_view_model_raises_the_artwork_model_id_when_the_model_changes_even_within_a_group()
    {
        var (vm, transport) = await StartAsync();
        await using var _ = vm;
        Assert.Null(vm.ArtworkModelId);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        transport.SetPairedDevice(Payload.ProThree, "pro3");
        Assert.Equal((ushort?)Payload.ProThree, vm.ArtworkModelId);
        Assert.Contains(nameof(HubViewModel.ArtworkModelId), raised);

        raised.Clear();
        transport.SetPairedDevice(0x2420, "pro2"); // another model, same group
        Assert.Equal((ushort?)0x2420, vm.ArtworkModelId);
        Assert.Contains(nameof(HubViewModel.ArtworkModelId), raised);
        Assert.DoesNotContain(nameof(HubViewModel.ArtworkGroup), raised);
    }

    [Fact]
    public void Artwork_charging_cue_uses_a_fresh_raw_charging_observation()
    {
        var freshLevel = Observation<int>.At(82, Now);
        var staleCharging = Observation<bool>.At(true, Now - TimeSpan.FromMinutes(6));
        var staleComponent = new BatteryComponent(freshLevel, staleCharging);
        var state = CueState(new BatteryState(staleComponent, BatteryComponent.Unknown, BatteryComponent.Unknown), EarState.Unknown);
        var batteryItem = new BatteryItemViewModel("L", "Left earbud");
        batteryItem.Update(staleComponent, Now, isDemo: false);

        // BatteryItemViewModel.IsCharging can retain a true flag while its timestamp is stale.
        Assert.True(batteryItem.IsCharging);
        Assert.False(HubViewModel.HasFreshChargingObservation(state, Now));

        var freshComponent = staleComponent with { Charging = Observation<bool>.At(true, Now) };
        Assert.True(HubViewModel.HasFreshChargingObservation(
            CueState(new BatteryState(freshComponent, BatteryComponent.Unknown, BatteryComponent.Unknown), EarState.Unknown), Now));

        var notCharging = freshComponent with { Charging = Observation<bool>.At(false, Now) };
        Assert.False(HubViewModel.HasFreshChargingObservation(
            CueState(new BatteryState(notCharging, BatteryComponent.Unknown, BatteryComponent.Unknown), EarState.Unknown), Now));
    }

    [Fact]
    public void Artwork_wear_cue_from_a_broadcast_or_unsessioned_state_expires_after_fifteen_seconds()
    {
        var primaryStale = Observation<PodPlacement>.At(PodPlacement.InEar, Now - TimeSpan.FromSeconds(16));
        var primaryAged = Observation<PodPlacement>.At(PodPlacement.InEar, Now - TimeSpan.FromSeconds(14));
        var secondaryFresh = Observation<PodPlacement>.At(PodPlacement.InEar, Now);
        Assert.True(HubViewModel.HasFreshInEarObservation(
            CueState(BatteryState.Unknown, new EarState(primaryStale, secondaryFresh)), Now));
        Assert.True(HubViewModel.HasFreshInEarObservation(
            CueState(BatteryState.Unknown, new EarState(primaryAged, Observation<PodPlacement>.Unknown)), Now));
        Assert.False(HubViewModel.HasFreshInEarObservation(
            CueState(BatteryState.Unknown, new EarState(primaryStale, Observation<PodPlacement>.Unknown)), Now));

        var secondaryOut = Observation<PodPlacement>.At(PodPlacement.OutOfEar, Now);
        Assert.False(HubViewModel.HasFreshInEarObservation(
            CueState(BatteryState.Unknown, new EarState(primaryStale, secondaryOut)), Now));

        var future = Observation<PodPlacement>.At(PodPlacement.InEar, Now + TimeSpan.FromSeconds(2));
        Assert.False(HubViewModel.HasFreshInEarObservation(
            CueState(BatteryState.Unknown, new EarState(future, future)), Now));
    }

    [Fact]
    public void Artwork_wear_cue_is_held_while_a_control_session_is_open_and_never_expires()
    {
        var old = Observation<PodPlacement>.At(PodPlacement.InEar, Now - TimeSpan.FromMinutes(30));
        var ears = new EarState(old, Observation<PodPlacement>.Unknown);
        var open = SessionState(ears, sessionOpen: true);
        Assert.True(HubViewModel.HasFreshInEarObservation(open, Now));
        Assert.Null(HubViewModel.NextArtworkCueExpiry(open, Now));

        // Future-dated observations never count, even in a session.
        var future = Observation<PodPlacement>.At(PodPlacement.InEar, Now + TimeSpan.FromSeconds(2));
        Assert.False(HubViewModel.HasFreshInEarObservation(SessionState(new EarState(future, future), sessionOpen: true), Now));

        // The session ended: the old report no longer holds.
        Assert.False(HubViewModel.HasFreshInEarObservation(SessionState(ears, sessionOpen: false), Now));

        // A broadcast observation is never held, whatever its link says; it expires 15 s after the report.
        var broadcast = SessionState(new EarState(Observation<PodPlacement>.At(PodPlacement.InEar, Now), Observation<PodPlacement>.Unknown), sessionOpen: true)
            with { Capabilities = DeviceCapabilities.BroadcastObservation };
        Assert.Equal(Now + HubViewModel.ArtworkEarFreshness + TimeSpan.FromTicks(1), HubViewModel.NextArtworkCueExpiry(broadcast, Now));
        Assert.False(HubViewModel.HasFreshInEarObservation(broadcast, Now + TimeSpan.FromSeconds(16)));
    }

    private static DeviceState SessionState(EarState ears, bool sessionOpen)
        => CueState(BatteryState.Unknown, ears) with
        {
            Link = new LinkState(
                Observation<bool>.At(true, Now),
                Observation<bool>.At(false, Now),
                Observation<bool>.At(sessionOpen, Now),
                Observation<bool>.At(false, Now)),
        };

    private static DeviceState CueState(BatteryState battery, EarState ears)
        => new("dev", "AirPods", "AirPods", DataOrigin.Hardware, 1, Now, DeviceCapabilities.None,
            LinkState.Unknown, battery, ListeningState.Unknown, default, default)
        { Ears = ears };

    [Fact]
    public async Task An_unlisted_id_with_an_earbud_layout_gets_the_case_drawing()
    {
        var (vm, transport) = await StartAsync();
        await using var _ = vm;
        transport.SetPairedDevice(Payload.ProThree, "pro3");
        Assert.Equal(ArtworkGroup.ProEarbuds, vm.ArtworkGroup);
        transport.SetPairedDevice(0x7F20, "unknown");
        // The unlisted id reports the earbud layout, so the rings show a case and the drawing matches them.
        Assert.Equal(ArtworkGroup.StemEarbuds, vm.ArtworkGroup);
    }

    // ---- demo ----

    private static async Task<DeviceState> DemoStateAsync(ushort? model, DemoBatteryScenario battery = DemoBatteryScenario.Normal)
    {
        var scenario = TestSupport.Instant();
        scenario.ModelId = model;
        scenario.Battery = battery;
        await using var demo = new DemoAirPodsTransport(scenario);
        await demo.StartAsync();
        return demo.Current!;
    }

    [Fact]
    public async Task The_default_demo_is_still_airpods_pro_3_and_carries_its_id()
    {
        var s = await DemoStateAsync(null);
        Assert.Equal((ushort)0x2720, s.ModelId);
        Assert.Equal("AirPods Pro 3", s.DisplayName);
        Assert.Equal("AirPods Pro 3", s.ModelName);
        Assert.Equal(BatteryLayout.EarbudsWithCase, s.Layout);
        Assert.Equal("AirPods", s.DeviceNoun);
        Assert.Equal(80, s.Battery.Left.Level.Value);
        Assert.Equal(70, s.Battery.Right.Level.Value);
        Assert.Equal(60, s.Battery.Case.Level.Value);
    }

    [Fact]
    public async Task A_studio_pro_demo_reports_one_level_in_the_left_slot()
    {
        var s = await DemoStateAsync(StudioPro);
        Assert.Equal((ushort?)StudioPro, s.ModelId);
        Assert.Equal("Beats Studio Pro", s.DisplayName);
        Assert.Equal(BatteryLayout.SingleBattery, s.Layout);
        Assert.Equal("Beats", s.DeviceNoun);
        Assert.Equal(80, s.Battery.Left.Level.Value);
        Assert.False(s.Battery.Right.Level.HasValue);
        Assert.False(s.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task A_solo_buds_demo_has_two_levels_and_no_case()
    {
        var s = await DemoStateAsync(SoloBuds);
        Assert.Equal(BatteryLayout.EarbudsNoCaseBattery, s.Layout);
        Assert.True(s.Battery.Left.Level.HasValue);
        Assert.True(s.Battery.Right.Level.HasValue);
        Assert.False(s.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task An_unlisted_demo_id_is_single_battery_headphones()
    {
        var s = await DemoStateAsync(0x7F20);
        Assert.Equal(BatteryLayout.SingleBattery, s.Layout);
        Assert.Equal("headphones", s.DeviceNoun);
        Assert.Equal(AppleModelCatalog.DisplayNameOf(0x7F20), s.DisplayName);
        Assert.False(s.Battery.Case.Level.HasValue);
    }

    [Theory]
    [InlineData(DemoBatteryScenario.Low, 10)]
    [InlineData(DemoBatteryScenario.Stale, 80)]
    public async Task Low_and_stale_scenarios_follow_the_layout_too(DemoBatteryScenario scenario, int left)
    {
        var s = await DemoStateAsync(StudioPro, scenario);
        Assert.Equal(left, s.Battery.Left.Level.Value);
        Assert.False(s.Battery.Right.Level.HasValue);
        Assert.False(s.Battery.Case.Level.HasValue);
    }

    [Fact]
    public void Contract_is_1_4() => Assert.Equal("1.4", HubContract.Version);
}
