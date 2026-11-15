using AppleDeviceHub.Core.Connection;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Connection;

public sealed class CompanionConnectionViewModelTests
{
    private const string TargetContainer = "0a91ec1f-5b8e-49cf-b411-e3e359b34a77";
    private const string OtherContainer = "f6a76cac-39d4-49c8-99cb-011e272ffab0";
    private const string MachineContainer = "00000000-0000-0000-ffff-ffffffffffff";

    [Fact]
    public void Container_identity_rejects_empty_machine_and_malformed_ids()
    {
        Assert.True(ConnectionIdentity.IsUsableContainerId(TargetContainer));
        Assert.False(ConnectionIdentity.IsUsableContainerId(Guid.Empty.ToString()));
        Assert.False(ConnectionIdentity.IsUsableContainerId(MachineContainer));
        Assert.False(ConnectionIdentity.IsUsableContainerId("not-a-container"));
        Assert.Equal(TargetContainer, ConnectionIdentity.NormalizeContainerId(TargetContainer.ToUpperInvariant()));
    }

    [Fact]
    public async Task Connection_state_uses_exact_container_and_never_a_matching_name()
    {
        var settings = new HubSettings
        {
            AirPodsDeviceId = "paired-target",
            AudioTargetEndpointId = "saved-output",
            AudioTargetContainerId = OtherContainer,
            RestorePreviousAudioOutput = true,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer)],
                [
                    Endpoint("foreign-active", "AirPods Pro", OtherContainer, available: true, reconnect: true),
                    Endpoint("target-idle", "Speakers", TargetContainer, available: false, reconnect: true),
                ]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsCurrentOutput);
        Assert.True(viewModel.CanConnect);
        Assert.Equal("paired-target", viewModel.SelectedDeviceId);
        Assert.Equal("saved-output", settings.AudioTargetEndpointId);
        Assert.Equal(OtherContainer, settings.AudioTargetContainerId);
        Assert.True(settings.RestorePreviousAudioOutput);
    }

    [Fact]
    public async Task Invalid_or_machine_container_devices_are_never_selectable()
    {
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [
                    Device("empty", "AirPods", Guid.Empty.ToString()),
                    Device("machine", "AirPods", MachineContainer),
                    Device("malformed", "AirPods", "invalid"),
                ],
                [
                    Endpoint("empty-out", "AirPods", Guid.Empty.ToString(), available: true, reconnect: true),
                    Endpoint("machine-out", "AirPods", MachineContainer, available: true, reconnect: true),
                ]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, new HubSettings(), RunInline);

        await viewModel.RefreshAsync();

        Assert.Empty(viewModel.Devices);
        Assert.False(viewModel.HasSelectedDevice);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public async Task Active_endpoint_is_recognized_even_when_nearby_observation_is_off()
    {
        var settings = new HubSettings
        {
            AirPodsDeviceId = "paired-target",
            ObserveNearbyAirPods = false,
            AutoConnectAirPods = false,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods", TargetContainer)],
                [Endpoint("target", "AirPods Stereo", TargetContainer, available: true, currentOutput: true, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsConnected);
        Assert.True(viewModel.IsCurrentOutput);
        Assert.Equal(0, controller.ReconnectCalls);
    }

    [Fact]
    public async Task Explicit_device_selection_does_not_change_the_audio_restoration_target()
    {
        var settings = new HubSettings
        {
            AudioTargetEndpointId = "old-audio-target",
            AudioTargetContainerId = OtherContainer,
            RestorePreviousAudioOutput = true,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [
                    Device("paired-target", "AirPods Pro", TargetContainer),
                    Device("paired-other", "Other headset", OtherContainer),
                ],
                [
                    Endpoint("target", "AirPods Stereo", TargetContainer, available: false, reconnect: true),
                    Endpoint("other", "Other headset", OtherContainer, available: false, reconnect: true),
                ]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        await viewModel.RefreshAsync();

        viewModel.SelectedDeviceId = "paired-target";

        Assert.Equal("paired-target", settings.AirPodsDeviceId);
        Assert.Equal("old-audio-target", settings.AudioTargetEndpointId);
        Assert.Equal(OtherContainer, settings.AudioTargetContainerId);
        Assert.True(settings.RestorePreviousAudioOutput);
    }

    [Fact]
    public async Task Legacy_selection_migrates_only_from_a_unique_saved_endpoint_container()
    {
        var settings = new HubSettings
        {
            AudioTargetEndpointId = "saved-target",
            AudioTargetContainerId = TargetContainer,
            RestorePreviousAudioOutput = true,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer)],
                [
                    Endpoint("saved-target", "AirPods Stereo", TargetContainer, available: true, currentOutput: true, reconnect: true),
                    Endpoint("same-name-foreign", "AirPods Pro", OtherContainer, available: true, reconnect: true),
                ]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Equal("paired-target", viewModel.SelectedDeviceId);
        Assert.Equal("paired-target", settings.AirPodsDeviceId);
        Assert.Equal("saved-target", settings.AudioTargetEndpointId);
        Assert.Equal(TargetContainer, settings.AudioTargetContainerId);
        Assert.True(settings.RestorePreviousAudioOutput);
    }

    [Fact]
    public async Task Legacy_selection_does_not_migrate_when_the_container_is_ambiguous_or_only_the_name_matches()
    {
        var settings = new HubSettings
        {
            AudioTargetEndpointId = "saved-target",
            AudioTargetContainerId = TargetContainer,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [
                    Device("paired-a", "AirPods Pro", TargetContainer),
                    Device("paired-b", "AirPods Pro", TargetContainer),
                ],
                [
                    Endpoint("saved-target", "AirPods Stereo", TargetContainer, available: true, currentOutput: true, reconnect: true),
                ]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.SelectedDeviceId);
        Assert.Equal("", settings.AirPodsDeviceId);
        Assert.Equal("saved-target", settings.AudioTargetEndpointId);
        Assert.All(viewModel.Devices, device =>
        {
            Assert.False(device.IsConnected);
            Assert.False(device.IsCurrentOutput);
            Assert.False(device.CanReconnect);
            Assert.Contains("can't tell which device", device.Detail ?? "");
        });
    }

    [Theory]
    [InlineData(false, false, IdentityConfidence.LikelyYours, false)]
    [InlineData(false, true, IdentityConfidence.LikelyYours, false)]
    [InlineData(true, false, IdentityConfidence.LikelyYours, false)]
    [InlineData(true, true, IdentityConfidence.Unknown, false)] // ambiguous or unpaired source
    [InlineData(true, true, IdentityConfidence.Probable, false)] // unique model candidate without an independent clue
    [InlineData(true, true, IdentityConfidence.LikelyYours, true)]
    public async Task Automatic_connect_requires_both_opt_ins_and_one_fresh_episode(
        bool observeNearby,
        bool autoConnect,
        IdentityConfidence nearbyIdentity,
        bool expectedAttempt)
    {
        var settings = new HubSettings
        {
            AirPodsDeviceId = "paired-target",
            ObserveNearbyAirPods = observeNearby,
            AutoConnectAirPods = autoConnect,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer)],
                [Endpoint("target", "AirPods Stereo", TargetContainer, available: false, reconnect: true)]),
            Reconnect = async (_, _, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { }
                return new(ConnectionAttemptKind.Failed, "test operation ended");
            },
        };
        var viewModel = new CompanionConnectionViewModel(
            controller,
            settings,
            RunInline,
            autoConnectIdentity: () => nearbyIdentity);
        await viewModel.RefreshAsync();

        viewModel.NotifyNearbyEpisode(true);
        viewModel.NotifyNearbyEpisode(true);

        Assert.Equal(expectedAttempt ? 1 : 0, controller.ReconnectCalls);
        viewModel.Dispose();
        await TestSupport.WaitUntil(() => controller.CompletedReconnects == controller.ReconnectCalls);
    }

    [Fact]
    public async Task Automatic_connect_retries_when_identity_becomes_likely_during_the_same_open_episode()
    {
        var settings = new HubSettings
        {
            AirPodsDeviceId = "paired-target",
            ObserveNearbyAirPods = true,
            AutoConnectAirPods = true,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer)],
                [Endpoint("target", "AirPods Stereo", TargetContainer, available: false, reconnect: true)]),
            Reconnect = (_, _, _) => Task.FromResult(new ConnectionAttemptResult(
                ConnectionAttemptKind.Failed, "the attempt was counted and safely completed")),
        };
        var identity = IdentityConfidence.Unknown;
        using var viewModel = new CompanionConnectionViewModel(
            controller,
            settings,
            RunInline,
            autoConnectIdentity: () => identity);
        await viewModel.RefreshAsync();

        var now = DateTimeOffset.UtcNow;
        var source = new BroadcastSource(
            Guid.NewGuid(), 1, 0x200F, "AirPods Pro", -55, 70, 60, 80, now,
            ThisPodInCase: true, LidClosed: false, LidCounter: 1, Identity: IdentityConfidence.Unknown);
        var policy = new NearbyPromptPolicy();

        var pending = policy.Evaluate([source], BroadcastPhase.Fresh, observing: true, now);
        Assert.False(pending.HasUniqueFreshOpenSource);
        var settled = policy.Evaluate(
            [source with { LastHeardAt = now.AddSeconds(1) }],
            BroadcastPhase.Fresh,
            observing: true,
            now.AddSeconds(1));
        Assert.True(settled.ShouldNotifyNearbyEpisodePresent);
        Assert.True(settled.HasUniqueFreshOpenSource);
        viewModel.NotifyNearbyEpisode(settled.HasUniqueFreshOpenSource);
        Assert.Equal(0, controller.ReconnectCalls);

        identity = IdentityConfidence.LikelyYours;
        var upgradedSource = source with
        {
            LastHeardAt = now.AddSeconds(2),
            Identity = identity,
        };
        var upgraded = policy.Evaluate(
            [upgradedSource], BroadcastPhase.Fresh, observing: true, now.AddSeconds(2));
        Assert.True(upgraded.HasUniqueFreshOpenSource);
        Assert.False(upgraded.ShouldPrompt);
        Assert.False(upgraded.ShouldNotifyNearbyEpisodePresent);
        viewModel.NotifyNearbyEpisode(upgraded.HasUniqueFreshOpenSource);
        Assert.Equal(1, controller.ReconnectCalls);

        var repeated = policy.Evaluate(
            [upgradedSource with { LastHeardAt = now.AddSeconds(3) }],
            BroadcastPhase.Fresh,
            observing: true,
            now.AddSeconds(3));
        Assert.True(repeated.HasUniqueFreshOpenSource);
        viewModel.NotifyNearbyEpisode(repeated.HasUniqueFreshOpenSource);
        Assert.Equal(1, controller.ReconnectCalls);
    }

    [Fact]
    public async Task Explicit_connect_remains_available_without_nearby_pairing_evidence()
    {
        var settings = new HubSettings
        {
            AirPodsDeviceId = "paired-target",
            ObserveNearbyAirPods = true,
            AutoConnectAirPods = true,
        };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer)],
                [Endpoint("target", "AirPods Stereo", TargetContainer, available: false, reconnect: true)]),
            Reconnect = (_, _, _) => Task.FromResult(new ConnectionAttemptResult(
                ConnectionAttemptKind.Unsupported, "manual attempt remains explicit")),
        };
        using var viewModel = new CompanionConnectionViewModel(
            controller,
            settings,
            RunInline,
            autoConnectIdentity: () => IdentityConfidence.Unknown);
        await viewModel.RefreshAsync();

        viewModel.NotifyNearbyEpisode(true);
        Assert.Equal(0, controller.ReconnectCalls);

        await viewModel.ConnectAsync();

        Assert.Equal(1, controller.ReconnectCalls);
    }

    [Fact]
    public async Task Late_result_from_old_selection_cannot_clear_a_new_connecting_state()
    {
        var completions = new List<TaskCompletionSource<ConnectionAttemptResult>>();
        var settings = new HubSettings();
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [
                    Device("paired-a", "AirPods A", TargetContainer),
                    Device("paired-b", "AirPods B", OtherContainer),
                ],
                [
                    Endpoint("output-a", "AirPods A", TargetContainer, available: false, reconnect: true),
                    Endpoint("output-b", "AirPods B", OtherContainer, available: false, reconnect: true),
                ]),
            Reconnect = (_, _, _) =>
            {
                var completion = new TaskCompletionSource<ConnectionAttemptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                completions.Add(completion);
                return completion.Task;
            },
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        await viewModel.RefreshAsync();

        viewModel.SelectedDeviceId = "paired-a";
        var oldAttempt = viewModel.ConnectAsync();
        Assert.True(viewModel.IsConnecting);

        viewModel.SelectedDeviceId = "paired-b";
        var currentAttempt = viewModel.ConnectAsync();
        Assert.True(viewModel.IsConnecting);
        Assert.Equal(2, completions.Count);

        completions[0].SetResult(new(ConnectionAttemptKind.Failed, "late old-device failure"));
        await oldAttempt;
        Assert.True(viewModel.IsConnecting);
        Assert.DoesNotContain("late old-device failure", viewModel.DetailText);

        completions[1].SetResult(new(ConnectionAttemptKind.Failed, "current-device failure"));
        await currentAttempt;
        Assert.False(viewModel.IsConnecting);
        Assert.Contains("current-device failure", viewModel.DetailText);
    }

    [Fact]
    public async Task Connect_attempt_is_confirmed_only_after_a_later_active_endpoint_snapshot()
    {
        var attempt = new TaskCompletionSource<ConnectionAttemptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer)],
                [Endpoint("target", "AirPods Stereo", TargetContainer, available: false, reconnect: true)]),
            Reconnect = (_, _, _) => attempt.Task,
        };
        using var viewModel = new CompanionConnectionViewModel(controller, new HubSettings
        {
            AirPodsDeviceId = "paired-target",
        }, RunInline);
        await viewModel.RefreshAsync();

        var connect = viewModel.ConnectAsync();
        Assert.False(viewModel.IsConnected);
        attempt.SetResult(new(ConnectionAttemptKind.Attempted, "attempt accepted"));
        await TestSupport.WaitUntil(() => controller.ReadCalls >= 2);
        Assert.False(viewModel.IsConnected);
        Assert.True(viewModel.IsConnecting);

        controller.Snapshot = Snapshot(
            [Device("paired-target", "AirPods Pro", TargetContainer)],
            [Endpoint("target", "AirPods Stereo", TargetContainer, available: true, currentOutput: true, reconnect: true)]);
        await viewModel.RefreshAsync();
        await TestSupport.WaitUntil(() => !viewModel.IsConnecting);

        Assert.True(viewModel.IsConnected);
        Assert.True(viewModel.IsCurrentOutput);
        await connect;
    }

    [Fact]
    public async Task Selected_device_reads_the_complete_snapshot_while_the_bound_list_refreshes()
    {
        var settings = new HubSettings { AirPodsDeviceId = "paired-target" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer)],
                [Endpoint("target", "AirPods Stereo", TargetContainer, available: true, currentOutput: true, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        var checkedDuringReset = false;
        viewModel.Devices.CollectionChanged += (_, change) =>
        {
            if (change.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset) return;
            checkedDuringReset = true;
            Assert.Equal("paired-target", viewModel.SelectedDevice?.Id);
            Assert.True(viewModel.IsConnected);
        };

        await viewModel.RefreshAsync();

        Assert.True(checkedDuringReset);
    }

    [Fact]
    public async Task Disposing_during_a_pending_refresh_does_not_dispose_its_semaphore_or_leak_cancellation()
    {
        var pendingRead = new TaskCompletionSource<ConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new FakeConnectionController
        {
            Snapshot = ConnectionSnapshot.Incomplete("unused"),
            Read = _ => pendingRead.Task,
        };
        var viewModel = new CompanionConnectionViewModel(controller, new HubSettings(), RunInline);

        var refresh = viewModel.RefreshAsync();
        viewModel.Dispose();
        await refresh;

        pendingRead.SetResult(Snapshot([], []));
    }

    [Fact]
    public async Task Complete_snapshots_raise_SnapshotApplied_with_the_selected_device_and_incomplete_ones_do_not()
    {
        var settings = new HubSettings { AirPodsDeviceId = "paired-target" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("paired-target", "AirPods Pro", TargetContainer) with { ModelId = 0x2720, WindowsBatteryPercent = 60 }],
                [Endpoint("target", "AirPods Stereo", TargetContainer, available: true, reconnect: true, currentOutput: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        var seen = new List<(ushort? Model, int? Battery, bool Connected)>();
        viewModel.SnapshotApplied += (_, _) =>
            seen.Add((viewModel.SelectedDevice?.ModelId, viewModel.SelectedDevice?.WindowsBatteryPercent, viewModel.IsConnected));

        await viewModel.RefreshAsync();
        Assert.Equal([((ushort?)0x2720, (int?)60, true)], seen);

        controller.Snapshot = ConnectionSnapshot.Incomplete("Windows is busy");
        await viewModel.RefreshAsync();
        Assert.Single(seen);
    }

    [Fact]
    public void Playback_output_projection_prefers_multimedia_and_falls_back_to_console()
    {
        Assert.Equal("multimedia", ConnectionOutputProjection.SelectPlaybackOutputId("multimedia", "console"));
        Assert.Equal("console", ConnectionOutputProjection.SelectPlaybackOutputId(null, "console"));
        Assert.Equal("console", ConnectionOutputProjection.SelectPlaybackOutputId("", "console"));
        Assert.Null(ConnectionOutputProjection.SelectPlaybackOutputId(null, null));
    }

    [Fact]
    public void Connection_preferences_round_trip_and_old_settings_default_to_off()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClaspConnectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new HubSettings
            {
                AirPodsDeviceId = "paired-target",
                ObserveNearbyAirPods = true,
                AutoConnectAirPods = true,
            };
            Assert.True(HubSettingsStore.Save(settings, path));

            var loaded = HubSettingsStore.Load(path);
            Assert.Equal("paired-target", loaded.AirPodsDeviceId);
            Assert.True(loaded.ObserveNearbyAirPods);
            Assert.True(loaded.AutoConnectAirPods);

            File.WriteAllText(path,
                "{\"Theme\":0,\"ReduceMotion\":false,\"ReduceTransparency\":false,\"AutoShowCaseCard\":true,\"CaseCardSeconds\":6}");
            var oldSettings = HubSettingsStore.Load(path);
            Assert.Equal("", oldSettings.AirPodsDeviceId);
            Assert.True(oldSettings.ObserveNearbyAirPods); // nearby detection is on by default now
            Assert.False(oldSettings.AutoConnectAirPods);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_only_paired_AirPods_is_selected_automatically_by_model_id()
    {
        var settings = new HubSettings();
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [
                    Device("airpods", "Living room headphones", TargetContainer) with { ModelId = 0x2720 },
                    Device("beats", "Beats Studio Pro", OtherContainer) with { ModelId = 0x2017 },
                ],
                [
                    Endpoint("airpods-out", "Headphones", TargetContainer, available: false, reconnect: true),
                    Endpoint("beats-out", "Headphones", OtherContainer, available: false, reconnect: true),
                ]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Equal("airpods", viewModel.SelectedDeviceId);
        Assert.Equal("airpods", settings.AirPodsDeviceId);
        Assert.True(viewModel.CanConnect);
        Assert.False(viewModel.NeedsPairing);
    }

    [Fact]
    public async Task Two_paired_AirPods_or_a_cleared_selection_are_never_auto_selected()
    {
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("first", "AirPods Pro", TargetContainer), Device("second", "AirPods Max", OtherContainer)],
                [
                    Endpoint("first-out", "Headphones", TargetContainer, available: false, reconnect: true),
                    Endpoint("second-out", "Headphones", OtherContainer, available: false, reconnect: true),
                ]),
        };
        using var ambiguous = new CompanionConnectionViewModel(controller, new HubSettings(), RunInline);
        await ambiguous.RefreshAsync();
        Assert.Null(ambiguous.SelectedDeviceId);

        controller.Snapshot = Snapshot(
            [Device("first", "AirPods Pro", TargetContainer)],
            [Endpoint("first-out", "Headphones", TargetContainer, available: false, reconnect: true)]);
        using var cleared = new CompanionConnectionViewModel(controller, new HubSettings(), RunInline);
        await cleared.RefreshAsync();
        Assert.Equal("first", cleared.SelectedDeviceId);
        cleared.ClearSelection();
        await cleared.RefreshAsync();
        Assert.Null(cleared.SelectedDeviceId);
    }

    [Fact]
    public async Task No_paired_AirPods_with_an_audio_endpoint_means_pairing_is_needed()
    {
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("speaker", "JBL Charge 6", TargetContainer)],
                [Endpoint("speaker-out", "Speakers", TargetContainer, available: true, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, new HubSettings(), RunInline);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.NeedsPairing);
        Assert.Null(viewModel.SelectedDeviceId);
    }

    [Fact]
    public async Task Windows_battery_is_shown_only_while_the_selected_AirPods_are_connected()
    {
        var settings = new HubSettings { AirPodsDeviceId = "airpods" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer) with { WindowsBatteryPercent = 80 }],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: true, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();
        Assert.Equal(80, viewModel.WindowsBatteryPercent);
        Assert.Equal("Battery 80%", viewModel.WindowsBatteryText);
        Assert.True(viewModel.WindowsBattery.IsKnown);
        Assert.Equal("80%", viewModel.WindowsBattery.PercentText);

        controller.Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer) with { WindowsBatteryPercent = 80 }],
            [Endpoint("airpods-out", "Headphones", TargetContainer, available: false, reconnect: true)]);
        await viewModel.RefreshAsync();
        Assert.Null(viewModel.WindowsBatteryPercent);
        Assert.Equal("", viewModel.WindowsBatteryText);
        Assert.False(viewModel.WindowsBattery.IsKnown);
        Assert.Equal("—", viewModel.WindowsBattery.PercentText);
    }

    [Fact]
    public async Task Missing_battery_is_explained_only_when_Windows_lists_no_Handsfree_service()
    {
        var settings = new HubSettings { AirPodsDeviceId = "airpods" };
        var connectedEndpoint = Endpoint("airpods-out", "Headphones", TargetContainer, available: true, reconnect: true);
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer) with { HasHandsfreeService = false }],
                [connectedEndpoint]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();
        Assert.Equal("", viewModel.WindowsBatteryText);
        Assert.Contains("Handsfree is off", viewModel.WindowsBatteryHint, StringComparison.Ordinal);

        // Unknown node list or a Handsfree service that has not reported yet: no claim.
        foreach (var handsfree in new bool?[] { null, true })
        {
            controller.Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer) with { HasHandsfreeService = handsfree }],
                [connectedEndpoint]);
            await viewModel.RefreshAsync();
            Assert.Equal("", viewModel.WindowsBatteryHint);
        }
    }

    [Fact]
    public async Task A_user_connect_is_announced_before_the_request_reaches_Windows()
    {
        var settings = new HubSettings { AirPodsDeviceId = "airpods" };
        var order = new List<string>();
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer)],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: false, reconnect: true)]),
            Reconnect = (_, _, _) =>
            {
                order.Add("reconnect");
                return Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Unsupported, "test"));
            },
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        viewModel.UserConnectRequested += (_, _) => order.Add("announced");
        await viewModel.RefreshAsync();

        await viewModel.ConnectAsync();

        Assert.Equal(["announced", "reconnect"], order);
    }

    [Fact]
    public async Task A_nearby_auto_connect_is_marked_before_the_request_and_ended_after_it()
    {
        var settings = new HubSettings
        {
            AirPodsDeviceId = "airpods",
            ObserveNearbyAirPods = true,
            AutoConnectAirPods = true,
        };
        var order = new List<string>();
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer)],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: false, reconnect: true)]),
            Reconnect = (_, _, _) =>
            {
                order.Add("reconnect");
                return Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Failed, "test"));
            },
        };
        using var viewModel = new CompanionConnectionViewModel(
            controller, settings, RunInline, autoConnectIdentity: () => IdentityConfidence.LikelyYours);
        viewModel.AutoConnectStarted += (_, _) => order.Add("auto-started");
        viewModel.AutoConnectEnded += (_, _) => order.Add("auto-ended");
        viewModel.UserConnectRequested += (_, _) => order.Add("user");
        await viewModel.RefreshAsync();

        viewModel.NotifyNearbyEpisode(true);
        await TestSupport.WaitUntil(() => order.Contains("auto-ended"));

        Assert.Equal(["auto-started", "reconnect", "auto-ended"], order);
    }

    [Fact]
    public async Task Disconnect_asks_Windows_once_and_reports_the_confirmed_disconnect()
    {
        var settings = new HubSettings { AirPodsDeviceId = "airpods" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer)],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: true, currentOutput: true, reconnect: true)]),
        };
        controller.Disconnect = (_, _, _) =>
        {
            controller.Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer)],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: false, reconnect: true)]);
            return Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Attempted, "accepted"));
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        var confirmed = 0;
        viewModel.UserDisconnectConfirmed += (_, _) => confirmed++;
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanDisconnect);
        Assert.False(viewModel.CanConnect);

        await viewModel.DisconnectAsync();

        Assert.Equal(1, controller.DisconnectCalls);
        Assert.Equal(1, confirmed);
        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsDisconnecting);
        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public async Task A_device_with_missing_drivers_stays_disabled_and_explains_why()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [new ConnectionDevice("gone", "AirPods Pro", TargetContainer, true, false, false, false, ConnectionDriverState.MissingDriverDetail)],
                [Endpoint("gone-out", "Headphones", TargetContainer, available: false, reconnect: false)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.CanConnect);
        Assert.Equal("Windows can't connect this device", viewModel.StatusText);
        Assert.Equal(ConnectionDriverState.MissingDriverDetail, viewModel.DetailText);
    }

    private static ConnectionDevice Driverless(string id, string name, string containerId, string? detail = null)
        => new(id, name, containerId, true, false, false, false, detail ?? ConnectionDriverState.MissingDriverDetail);

    private static ConnectionSnapshot DriverlessSnapshot(params ConnectionDevice[] working)
    {
        var devices = new List<ConnectionDevice> { Driverless("gone", "Old AirPods", TargetContainer) };
        var endpoints = new List<ConnectionAudioEndpoint>
        {
            Endpoint("gone-out", "Headphones", TargetContainer, available: false, reconnect: false),
        };
        foreach (var device in working)
        {
            devices.Add(device);
            endpoints.Add(Endpoint(device.Id + "-out", "Headphones", device.ContainerId, available: false, reconnect: true));
        }
        return Snapshot(devices, endpoints);
    }

    [Fact]
    public async Task A_driverless_selection_switches_to_the_one_working_AirPods()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var controller = new FakeConnectionController
        {
            Snapshot = DriverlessSnapshot(Device("good", "AirPods Pro", OtherContainer)),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Equal("good", settings.AirPodsDeviceId);
        Assert.Equal("good", viewModel.SelectedDeviceId);
        Assert.True(viewModel.CanConnect);
        Assert.Equal("Switched to AirPods Pro because Windows can't find the drivers for Old AirPods.", viewModel.DetailText);
    }

    [Fact]
    public async Task The_switch_message_shows_even_when_the_replacement_is_already_connected()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Driverless("gone", "Old AirPods", TargetContainer), Device("good", "AirPods Pro", OtherContainer)],
                [
                    Endpoint("gone-out", "Headphones", TargetContainer, available: false, reconnect: false),
                    Endpoint("good-out", "Headphones", OtherContainer, available: true, reconnect: true),
                ]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        const string message = "Switched to AirPods Pro because Windows can't find the drivers for Old AirPods.";

        await viewModel.RefreshAsync();

        Assert.Equal("good", viewModel.SelectedDeviceId);
        Assert.True(viewModel.IsConnected);
        Assert.Equal(message, viewModel.DetailText);

        await viewModel.RefreshAsync();

        Assert.NotEqual(message, viewModel.DetailText);
    }

    [Fact]
    public async Task A_driverless_selection_does_not_switch_when_two_AirPods_work()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var controller = new FakeConnectionController
        {
            Snapshot = DriverlessSnapshot(
                Device("good1", "AirPods Pro", OtherContainer),
                Device("good2", "AirPods Max", "5b1d3a42-8c7e-4f10-9a6b-2d4e8f0c1a35")),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Equal("gone", viewModel.SelectedDeviceId);
        Assert.Equal("gone", settings.AirPodsDeviceId);
    }

    [Fact]
    public async Task A_selection_with_some_other_problem_text_is_not_switched()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var snapshot = DriverlessSnapshot(Device("good", "AirPods Pro", OtherContainer));
        var controller = new FakeConnectionController
        {
            Snapshot = snapshot with
            {
                Devices = [Driverless("gone", "Old AirPods", TargetContainer, "Something else went wrong."), snapshot.Devices[1]],
            },
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Equal("gone", viewModel.SelectedDeviceId);
        Assert.Equal("gone", settings.AirPodsDeviceId);
    }

    [Fact]
    public async Task Picking_the_driverless_device_again_is_respected_for_the_session()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var controller = new FakeConnectionController
        {
            Snapshot = DriverlessSnapshot(Device("good", "AirPods Pro", OtherContainer)),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        await viewModel.RefreshAsync();
        Assert.Equal("good", viewModel.SelectedDeviceId);

        viewModel.SelectedDeviceId = "gone";
        await viewModel.RefreshAsync();

        Assert.Equal("gone", viewModel.SelectedDeviceId);
        Assert.Equal("gone", settings.AirPodsDeviceId);
    }

    [Fact]
    public async Task A_driverless_selection_switches_to_a_lone_listed_Beats_model()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var controller = new FakeConnectionController
        {
            Snapshot = DriverlessSnapshot(Device("beats", "Studio", OtherContainer) with { ModelId = 0x0B20 }),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Equal("beats", viewModel.SelectedDeviceId);
        Assert.Equal("beats", settings.AirPodsDeviceId);
    }

    [Fact]
    public async Task A_driverless_selection_prefers_AirPods_over_Beats()
    {
        var settings = new HubSettings { AirPodsDeviceId = "gone" };
        var controller = new FakeConnectionController
        {
            Snapshot = DriverlessSnapshot(
                Device("beats", "Studio", OtherContainer) with { ModelId = 0x0B20 },
                Device("good", "AirPods Pro", "5b1d3a42-8c7e-4f10-9a6b-2d4e8f0c1a35")),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);

        await viewModel.RefreshAsync();

        Assert.Equal("good", viewModel.SelectedDeviceId);
        Assert.Equal("good", settings.AirPodsDeviceId);
    }

    [Fact]
    public async Task A_rejected_disconnect_reports_the_reason_and_keeps_the_connection()
    {
        var settings = new HubSettings { AirPodsDeviceId = "airpods" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer)],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: true, reconnect: true)]),
            Disconnect = (_, _, _) => Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Unsupported, "Windows refused.")),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        var confirmed = 0;
        viewModel.UserDisconnectConfirmed += (_, _) => confirmed++;
        await viewModel.RefreshAsync();

        await viewModel.DisconnectAsync();

        Assert.Equal(0, confirmed);
        Assert.True(viewModel.IsConnected);
        Assert.Equal("Windows refused.", viewModel.DetailText);
    }

    [Fact]
    public async Task Refreshing_with_the_same_selection_does_not_raise_SelectedDeviceChanged()
    {
        var settings = new HubSettings { AirPodsDeviceId = "airpods" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer)],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: false, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        var changed = 0;
        var propertyRaised = 0;
        viewModel.SelectedDeviceChanged += (_, _) => changed++;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompanionConnectionViewModel.SelectedDeviceId)) propertyRaised++;
        };

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        Assert.Equal("airpods", viewModel.SelectedDeviceId);
        Assert.Equal(0, changed);
        Assert.True(propertyRaised >= 3);
    }

    [Fact]
    public async Task Selecting_a_different_device_raises_SelectedDeviceChanged_once()
    {
        var settings = new HubSettings { AirPodsDeviceId = "first" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot(
                [Device("first", "AirPods Pro", TargetContainer), Device("second", "AirPods Max", OtherContainer)],
                [Endpoint("first-out", "Headphones", TargetContainer, available: false, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        await viewModel.RefreshAsync();
        var changed = 0;
        viewModel.SelectedDeviceChanged += (_, _) => changed++;

        viewModel.SelectedDeviceId = "second";
        viewModel.SelectedDeviceId = "second";

        Assert.Equal("second", viewModel.SelectedDeviceId);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Clearing_the_selection_raises_SelectedDeviceChanged_once()
    {
        var settings = new HubSettings { AirPodsDeviceId = "airpods" };
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("airpods", "AirPods Pro", TargetContainer)],
                [Endpoint("airpods-out", "Headphones", TargetContainer, available: false, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        await viewModel.RefreshAsync();
        var changed = 0;
        viewModel.SelectedDeviceChanged += (_, _) => changed++;

        viewModel.ClearSelection();

        Assert.Null(viewModel.SelectedDeviceId);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task A_refresh_after_an_explicit_clear_does_not_reselect_the_sole_AirPods()
    {
        var settings = new HubSettings();
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("only", "AirPods Pro", TargetContainer)],
                [Endpoint("only-out", "Headphones", TargetContainer, available: false, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, settings, RunInline);
        await viewModel.RefreshAsync();
        Assert.Equal("only", viewModel.SelectedDeviceId); // the policy picked it

        viewModel.ClearSelection();
        await viewModel.RefreshAsync();

        Assert.Null(viewModel.SelectedDeviceId);
        Assert.Equal("", settings.AirPodsDeviceId);
    }

    [Fact]
    public async Task Auto_selecting_the_sole_AirPods_raises_SelectedDeviceChanged_once()
    {
        var controller = new FakeConnectionController
        {
            Snapshot = Snapshot([Device("only", "AirPods Pro", TargetContainer)],
                [Endpoint("only-out", "Headphones", TargetContainer, available: false, reconnect: true)]),
        };
        using var viewModel = new CompanionConnectionViewModel(controller, new HubSettings(), RunInline);
        var changed = 0;
        viewModel.SelectedDeviceChanged += (_, _) => changed++;

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        Assert.Equal("only", viewModel.SelectedDeviceId);
        Assert.Equal(1, changed);
    }

    private static void RunInline(Action action) => action();

    private static ConnectionSnapshot Snapshot(
        IReadOnlyList<ConnectionDevice> devices,
        IReadOnlyList<ConnectionAudioEndpoint> endpoints)
        => new(true, devices, endpoints, null);

    private static ConnectionDevice Device(string id, string name, string containerId)
        => new(id, name, containerId, true, false, false, true, null);

    private static ConnectionAudioEndpoint Endpoint(
        string id,
        string name,
        string containerId,
        bool available,
        bool reconnect,
        bool currentOutput = false)
        => new(id, name, containerId, available, currentOutput, reconnect);

    private sealed class FakeConnectionController : IAirPodsConnectionController
    {
        private int _readCalls;
        private int _reconnectCalls;
        private int _completedReconnects;

        public ConnectionSnapshot Snapshot { get; set; } = CompanionConnectionViewModelTests.Snapshot([], []);
        public Func<CancellationToken, Task<ConnectionSnapshot>>? Read { get; set; }
        public Func<string, string, CancellationToken, Task<ConnectionAttemptResult>>? Reconnect { get; set; }
        public Func<string, string, CancellationToken, Task<ConnectionAttemptResult>>? Disconnect { get; set; }
        public int DisconnectCalls { get; private set; }
        public bool IsBusy { get; set; }
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public int ReconnectCalls => Volatile.Read(ref _reconnectCalls);
        public int CompletedReconnects => Volatile.Read(ref _completedReconnects);

        public event EventHandler? Changed;

        public Task<ConnectionSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            return Read?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
        }

        public async Task<ConnectionAttemptResult> ReconnectAsync(
            string deviceId,
            string expectedContainerId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reconnectCalls);
            try
            {
                return Reconnect is null
                    ? new(ConnectionAttemptKind.Failed, "No reconnect configured.")
                    : await Reconnect(deviceId, expectedContainerId, cancellationToken);
            }
            finally
            {
                Interlocked.Increment(ref _completedReconnects);
            }
        }

        public Task<ConnectionAttemptResult> DisconnectAsync(
            string deviceId,
            string expectedContainerId,
            CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            return Disconnect?.Invoke(deviceId, expectedContainerId, cancellationToken)
                ?? Task.FromResult(new ConnectionAttemptResult(ConnectionAttemptKind.Failed, "No disconnect configured."));
        }

        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
