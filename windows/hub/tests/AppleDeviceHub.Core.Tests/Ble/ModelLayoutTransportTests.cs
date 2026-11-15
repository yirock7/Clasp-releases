using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Contracts;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Ble;

// ATTRIBUTION: the Studio Pro, Max and Solo Buds status, pods and flags/case bytes come from the public test suites of
// CAPod (https://github.com/d4rken-org/capod) and MagicPodsCore (https://github.com/steam3d/MagicPodsCore), both GPL-3.0,
// padded to a full 27-byte frame. The Max USB-C frame and the second-pair levels are synthetic. None of these models has
// been tested on hardware for this project.

/// <summary>The transport passes the model's battery layout to <see cref="DeviceState"/> and never invents a case level for a model without one.</summary>
public sealed class ModelLayoutTransportTests : IAsyncDisposable
{
    private const ushort StudioPro = 0x1720;
    private const ushort SoloBuds = 0x2620;
    private const ushort MaxLightning = 0x0A20;
    private const ushort MaxUsbC = 0x1F20;
    private const ushort Beats = 0x2520; // Beats Solo 4
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdvertisementSource _source = new();
    private readonly BroadcastObservationTransport _transport;
    private DateTimeOffset RememberedAt => _clock.GetUtcNow().AddHours(-3);

    public ModelLayoutTransportTests() => _transport = new BroadcastObservationTransport(_source, _clock);

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync();

    private async Task StartAsync()
    {
        await _transport.StartAsync();
        await _transport.SendAsync(new ConnectCommand(Guid.NewGuid()));
    }

    private void EmitStudioPro(byte pods = 0x06, ulong address = 1)
        => _source.Emit(Payload.Sighting(Payload.Build(model: StudioPro, status: 0x00, pods: pods, flagsAndCase: 0x00), address: address));

    [Fact]
    public async Task A_studio_pro_frame_reaches_the_state_as_a_single_battery()
    {
        await StartAsync();
        EmitStudioPro();

        var s = _transport.Current!;
        Assert.Equal(BatteryLayout.SingleBattery, s.Layout);
        Assert.Equal(60, s.Battery.Left.Level.Value);
        Assert.False(s.Battery.Right.Level.HasValue);
        Assert.False(s.Battery.Case.Level.HasValue);
        Assert.False(s.Supports(DeviceCapabilities.EarDetection));
    }

    [Fact]
    public async Task While_waiting_the_layout_follows_the_selected_model()
    {
        _transport.SetPairedDevice(StudioPro, "studio");
        await StartAsync();
        Assert.Equal(BroadcastPhase.Waiting, _transport.Phase);
        Assert.Equal(BatteryLayout.SingleBattery, _transport.Current!.Layout);

        _transport.SetPairedDevice(Payload.ProThree, "pro3");
        Assert.Equal(BroadcastPhase.Waiting, _transport.Phase);
        Assert.Equal(BatteryLayout.EarbudsWithCase, _transport.Current!.Layout);
    }

    [Theory]
    [InlineData(StudioPro)]
    [InlineData(SoloBuds)]
    public async Task A_remembered_case_is_never_shown_for_a_model_without_a_case_battery(ushort model)
    {
        _transport.SetPairedDevice(model, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        var frame = Payload.Sighting(Payload.Build(model: model, status: 0x33, pods: 0x89, flagsAndCase: 0x80), address: 1);
        _source.Emit(frame);
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);

        // Setting it again while the frame is on screen, then stopping and starting, must not bring it back.
        _transport.SetRememberedCase(70, RememberedAt);
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
        await _transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        await StartAsync();
        _source.Emit(frame);
        Assert.False(_transport.Current!.Battery.Case.Level.HasValue);
    }

    [Fact]
    public async Task A_remembered_case_is_still_shown_for_pro_3()
    {
        _transport.SetPairedDevice(Payload.ProThree, "dev");
        _transport.SetRememberedCase(60, RememberedAt);
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: Payload.ProThree, pods: 0x58, flagsAndCase: 0x4F), address: 1));

        Assert.Equal(BatteryLayout.EarbudsWithCase, _transport.Current!.Layout);
        Assert.Equal(60, _transport.Current.Battery.Case.Level.Value);
    }

    [Fact]
    public async Task A_max_frame_has_no_case_no_right_and_does_not_look_like_an_open_case()
    {
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: MaxLightning, status: 0x62, pods: 0x04, flagsAndCase: 0x80, lid: 0x01), address: 1));

        var s = _transport.Current!;
        Assert.Equal(BatteryLayout.SingleBattery, s.Layout);
        Assert.Equal(40, s.Battery.Left.Level.Value);
        Assert.False(s.Battery.Right.Level.HasValue);
        Assert.False(s.Battery.Case.Level.HasValue);
        var source = Assert.Single(_transport.Sources);
        Assert.Equal(BatteryLayout.SingleBattery, source.Layout);
        Assert.Null(source.ThisPodInCase);
        Assert.Null(source.LidClosed);

        // The nearby prompt must not treat it as an open case, however long it stays.
        var policy = new NearbyPromptPolicy();
        var t0 = _clock.GetUtcNow();
        Assert.False(policy.Evaluate(_transport.Sources, _transport.Phase, observing: true, t0).ShouldPrompt);
        Assert.False(policy.Evaluate(_transport.Sources, _transport.Phase, observing: true, t0.AddSeconds(1)).ShouldPrompt);
        Assert.False(policy.Evaluate(_transport.Sources, _transport.Phase, observing: true, t0.AddSeconds(3)).ShouldPrompt);
    }

    [Theory]
    [InlineData((byte)0x62, PodPlacement.InEar)]
    [InlineData((byte)0x42, PodPlacement.OutOfEar)]
    public async Task A_max_reports_both_sides_worn_or_not_with_ear_detection(byte status, PodPlacement expected)
    {
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: MaxLightning, status: status, pods: 0x04, flagsAndCase: 0x80), address: 1));

        var s = _transport.Current!;
        Assert.True(s.Supports(DeviceCapabilities.EarDetection));
        Assert.Equal(expected, s.Ears.Primary.Value);
        Assert.Equal(expected, s.Ears.Secondary.Value);
    }

    [Fact]
    public async Task A_max_usb_c_frame_claims_no_ear_detection()
    {
        await StartAsync();
        _source.Emit(Payload.Sighting(Payload.Build(model: MaxUsbC, status: 0x2B, pods: 0x04, flagsAndCase: 0x80), address: 1));

        Assert.False(_transport.Current!.Supports(DeviceCapabilities.EarDetection));
        Assert.False(_transport.Current.Ears.Primary.HasValue);
    }

    [Fact]
    public async Task A_single_battery_device_reaches_likely_by_battery_from_the_windows_battery()
    {
        _transport.SetPairedDevice(StudioPro, "studio");
        await StartAsync();
        EmitStudioPro();
        Assert.Equal(IdentityConfidence.Probable, _transport.IdentityConfidence);

        _transport.SetWindowsBattery(62); // within a step of the broadcast 60
        Assert.Equal(IdentityConfidence.LikelyByBattery, _transport.IdentityConfidence); // battery alone never latches
    }

    [Fact]
    public async Task A_second_device_of_the_same_model_at_a_different_level_is_ambiguous()
    {
        _transport.SetPairedDevice(StudioPro, "studio");
        await StartAsync();
        EmitStudioPro(pods: 0x06, address: 1);
        EmitStudioPro(pods: 0x02, address: 2); // 20 % versus 60 %

        Assert.Equal(BroadcastPhase.Ambiguous, _transport.Phase);
        Assert.Equal(IdentityConfidence.Unknown, _transport.IdentityConfidence);
        Assert.All(_transport.Sources, s => Assert.Equal(IdentityConfidence.Unknown, s.Identity));
    }

    [Fact]
    public async Task Display_text_names_the_selected_family()
    {
        _transport.SetPairedDevice(Beats, "beats");
        await StartAsync();
        Assert.Equal("Listening for nearby Beats", _transport.Current!.DisplayName);

        _transport.SetPairedDevice(0x3F20, "unlisted");
        Assert.Equal("Listening for nearby headphones", _transport.Current!.DisplayName);

        _transport.SetPairedDevice(null);
        Assert.Equal("Listening for nearby AirPods", _transport.Current!.DisplayName);
    }

    [Fact]
    public async Task Selecting_a_model_while_not_observing_refreshes_layout_and_noun()
    {
        await _transport.StartAsync();
        int raised = 0;
        _transport.StateChanged += (_, _) => raised++;

        _transport.SetPairedDevice(StudioPro, "beats");
        Assert.Equal(BroadcastPhase.NotObserving, _transport.Phase);
        Assert.Equal(BatteryLayout.SingleBattery, _transport.Current!.Layout);
        Assert.Equal("Beats", _transport.Current.DeviceNoun);
        Assert.Equal(1, raised);

        _transport.SetPairedDevice(StudioPro, "beats"); // same selection: nothing new
        Assert.Equal(1, raised);

        _transport.SetPairedDevice(Payload.ProThree, "pro3");
        Assert.Equal(BatteryLayout.EarbudsWithCase, _transport.Current!.Layout);
        Assert.Equal("AirPods", _transport.Current.DeviceNoun);
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task Selecting_a_model_before_start_publishes_nothing()
    {
        int raised = 0;
        _transport.StateChanged += (_, _) => raised++;
        _transport.SetPairedDevice(StudioPro, "beats");
        Assert.Equal(0, raised);
        Assert.Null(_transport.Current);
        await _transport.StartAsync();
        Assert.Equal(BatteryLayout.SingleBattery, _transport.Current!.Layout);
    }

    [Fact]
    public async Task Display_text_for_a_likely_beats_and_for_not_observing()
    {
        _transport.SetPairedDevice(StudioPro, "studio");
        await StartAsync();
        EmitStudioPro();
        Assert.Equal("Your Beats (likely)", _transport.Current!.DisplayName);

        await _transport.SendAsync(new DisconnectCommand(Guid.NewGuid()));
        Assert.Equal("Beats broadcasts (not observing)", _transport.Current!.DisplayName);
    }
}
