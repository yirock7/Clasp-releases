using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Connection;

/// <summary>Auto-selection prefers AirPods, falls back to a lone listed Beats model, and never guesses between several.</summary>
public sealed class ModelAwareAutoSelectTests
{
    private const string C1 = "0a91ec1f-5b8e-49cf-b411-e3e359b34a77";
    private const string C2 = "f6a76cac-39d4-49c8-99cb-011e272ffab0";
    private const string C3 = "3c7e1a52-9d7b-4b50-a3c5-0d6cbb1a2f11";
    private const ushort ProThree = 0x2720;
    private const ushort ProTwo = 0x2420;
    private const ushort StudioPro = 0x1720;
    private const ushort SoloFour = 0x2520;

    private static ConnectionDevice Device(string id, string name, string container, ushort? model, bool endpoint = true)
        => new(id, name, container, endpoint, false, false, true, null, model);

    private static async Task<(CompanionConnectionViewModel Vm, HubSettings Settings)> RefreshAsync(params ConnectionDevice[] devices)
    {
        var endpoints = devices.Where(d => d.HasAvailableAudioEndpoint)
            .Select(d => new ConnectionAudioEndpoint(d.Id + "-out", "Headphones", d.ContainerId, false, false, true)).ToArray();
        var controller = new Controller(new ConnectionSnapshot(true, devices, endpoints, null));
        var settings = new HubSettings();
        var vm = new CompanionConnectionViewModel(controller, settings, action => action());
        await vm.RefreshAsync();
        return (vm, settings);
    }

    [Fact]
    public async Task AirPods_plus_Beats_picks_the_AirPods()
    {
        var (vm, _) = await RefreshAsync(Device("pods", "Pro", C1, ProThree), Device("beats", "Studio", C2, StudioPro));
        using var _vm = vm;
        Assert.Equal("pods", vm.SelectedDeviceId);
    }

    [Fact]
    public async Task A_lone_Beats_is_picked()
    {
        var (vm, settings) = await RefreshAsync(Device("beats", "Studio", C2, StudioPro));
        using var _vm = vm;
        Assert.Equal("beats", vm.SelectedDeviceId);
        Assert.Equal("beats", settings.AirPodsDeviceId);
        Assert.False(vm.NeedsPairing);
        Assert.Equal("Beats", vm.SelectedDeviceNoun);
        Assert.Equal("Beats", vm.WindowsBattery.Label);
        Assert.Equal("Beats, reported by Windows", vm.WindowsBattery.SpokenLabel);
    }

    [Fact]
    public async Task Two_Beats_pick_neither()
    {
        var (vm, _) = await RefreshAsync(Device("a", "Studio", C1, StudioPro), Device("b", "Solo", C2, SoloFour));
        using var _vm = vm;
        Assert.Null(vm.SelectedDeviceId);
        Assert.False(vm.NeedsPairing);
    }

    [Fact]
    public async Task Two_AirPods_plus_a_Beats_pick_nothing()
    {
        var (vm, _) = await RefreshAsync(Device("a", "Pro 3", C1, ProThree), Device("b", "Pro 2", C2, ProTwo), Device("beats", "Studio", C3, StudioPro));
        using var _vm = vm;
        Assert.Null(vm.SelectedDeviceId);
    }

    [Fact]
    public async Task A_Beats_without_an_audio_endpoint_is_not_picked_and_does_not_count()
    {
        var (vm, _) = await RefreshAsync(Device("beats", "Studio", C2, StudioPro, endpoint: false));
        using var _vm = vm;
        Assert.Null(vm.SelectedDeviceId);
        Assert.True(vm.NeedsPairing);
    }

    [Fact]
    public async Task Pairing_is_needed_with_no_supported_device()
    {
        var (vm, _) = await RefreshAsync(Device("speaker", "JBL", C1, null));
        using var _vm = vm;
        Assert.True(vm.NeedsPairing);
    }

    [Fact]
    public async Task The_windows_battery_label_defaults_to_AirPods()
    {
        var (vm, _) = await RefreshAsync(Device("pods", "Pro", C1, ProThree));
        using var _vm = vm;
        Assert.Equal("AirPods", vm.SelectedDeviceNoun);
        Assert.Equal("AirPods", vm.WindowsBattery.Label);
        Assert.Equal("AirPods, reported by Windows", vm.WindowsBattery.SpokenLabel);
        Assert.Equal("AirPods, reported by Windows, battery unknown", vm.WindowsBattery.AutomationName);
    }

    private sealed class Controller(ConnectionSnapshot snapshot) : IAirPodsConnectionController
    {
        public event EventHandler? Changed { add { } remove { } }
        public bool IsBusy => false;
        public Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task<ConnectionAttemptResult> ReconnectAsync(string deviceId, string expectedContainerId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Failed, "none"));
        public Task<ConnectionAttemptResult> DisconnectAsync(string deviceId, string expectedContainerId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Failed, "none"));
    }
}
