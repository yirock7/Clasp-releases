using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests;

public class AudioOutputRestorationTests
{
    private const string Group = "16dab7af-e793-432a-b981-c55d15be5f48";
    private static readonly AudioOutputRole[] Roles = Enum.GetValues<AudioOutputRole>();

    private static AudioOutputSnapshot Snapshot(string? output, bool airPodsAvailable = true, bool speakersAvailable = true,
        bool headsetProfileAvailable = false, string? communications = null)
        => new([
            new("pods", "AirPods stereo", airPodsAvailable, Group),
            new("pods-call", "AirPods hands free", headsetProfileAvailable, Group),
            new("speakers", "Speakers", speakersAvailable, null),
            new("monitor", "Monitor", true, null)],
            Roles.ToDictionary(role => role, role => role == AudioOutputRole.Communications && communications is not null ? communications : output));

    private static AudioOutputRestorationPolicy Policy(string? fallback = null)
    {
        var policy = new AudioOutputRestorationPolicy();
        policy.Configure(true, "pods", Group, fallback);
        return policy;
    }

    [Fact]
    public void Captures_previous_output_and_restores_each_role_after_real_endpoint_loss()
    {
        var policy = Policy();
        Assert.Empty(policy.Observe(Snapshot("speakers", false)));
        Assert.Empty(policy.Observe(Snapshot("pods")));
        Assert.True(policy.HasRememberedOutput);
        var requests = policy.Observe(Snapshot("monitor", false)); // Windows chose another fallback
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("speakers", request.RestoreEndpointId);
            Assert.Equal("monitor", request.ExpectedCurrentEndpointId);
            Assert.Contains("pods", request.TargetEndpointIds);
        });
        Assert.Empty(policy.Observe(Snapshot("monitor", false))); // no repeated output writes
    }

    [Fact]
    public void RestoreDestination_reports_the_remembered_output_without_changing_anything()
    {
        var policy = Policy();
        Assert.Null(policy.RestoreDestination(AudioOutputRole.Multimedia));
        policy.Observe(Snapshot("speakers"));
        Assert.Null(policy.RestoreDestination(AudioOutputRole.Multimedia)); // AirPods not yet the default
        policy.Observe(Snapshot("pods"));
        Assert.Equal("speakers", policy.RestoreDestination(AudioOutputRole.Multimedia));
        Assert.Equal("speakers", policy.RestoreDestination(AudioOutputRole.Console));
        Assert.True(policy.HasRememberedOutput);
        policy.Forget();
        Assert.Null(policy.RestoreDestination(AudioOutputRole.Multimedia));
    }

    [Fact]
    public void Observed_manual_change_while_airpods_remain_available_wins()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers"));
        policy.Observe(Snapshot("pods"));
        policy.Observe(Snapshot("monitor"));
        Assert.Empty(policy.Observe(Snapshot("monitor", false)));
    }

    [Fact]
    public void Does_not_switch_audio_just_because_default_changes_or_one_profile_disappears()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers"));
        policy.Observe(Snapshot("pods"));
        Assert.Empty(policy.Observe(Snapshot("pods-call", false, headsetProfileAvailable: true)));
        Assert.True(policy.HasRememberedOutput);
        Assert.Equal(3, policy.Observe(Snapshot("monitor", false)).Count);
    }

    [Fact]
    public void Staged_profile_loss_keeps_the_original_return_target_until_the_whole_device_disconnects()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers", headsetProfileAvailable: true));
        policy.Observe(Snapshot("pods", headsetProfileAvailable: true));

        Assert.Empty(policy.Observe(Snapshot("monitor", false, headsetProfileAvailable: true)));
        Assert.True(policy.HasRememberedOutput);

        var requests = policy.Observe(Snapshot("monitor", false, headsetProfileAvailable: false));
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal("speakers", request.RestoreEndpointId));
    }

    [Fact]
    public void Manual_choice_after_profile_switch_clears_the_claim_for_the_active_profile()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers", headsetProfileAvailable: true));
        policy.Observe(Snapshot("pods", headsetProfileAvailable: true));
        Assert.Empty(policy.Observe(Snapshot("pods-call", headsetProfileAvailable: true)));

        policy.Observe(Snapshot("monitor", headsetProfileAvailable: true));
        Assert.Empty(policy.Observe(Snapshot("monitor", airPodsAvailable: false, headsetProfileAvailable: false)));
    }

    [Fact]
    public void Stale_default_for_unavailable_profile_does_not_take_claim_ownership()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers", headsetProfileAvailable: true));
        policy.Observe(Snapshot("pods", headsetProfileAvailable: true));
        policy.Observe(Snapshot("pods-call", airPodsAvailable: true, headsetProfileAvailable: false));

        policy.Observe(Snapshot("monitor", airPodsAvailable: true, headsetProfileAvailable: false));
        Assert.Empty(policy.Observe(Snapshot("monitor", airPodsAvailable: false, headsetProfileAvailable: false)));
    }

    [Fact]
    public void Roles_keep_their_individual_previous_devices()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers", communications: "monitor"));
        policy.Observe(Snapshot("pods", communications: "pods-call", headsetProfileAvailable: true));
        var requests = policy.Observe(Snapshot(null, false));
        Assert.Equal("monitor", Assert.Single(requests, request => request.Role == AudioOutputRole.Communications).RestoreEndpointId);
        Assert.All(requests.Where(request => request.Role != AudioOutputRole.Communications), request => Assert.Equal("speakers", request.RestoreEndpointId));
    }

    [Fact]
    public void Already_connected_at_start_needs_explicit_fallback_and_never_guesses()
    {
        var policy = Policy();
        policy.Observe(Snapshot("pods"));
        Assert.Empty(policy.Observe(Snapshot("monitor", false)));
        policy = Policy("speakers");
        policy.Observe(Snapshot("pods"));
        Assert.Equal(3, policy.Observe(Snapshot("monitor", false)).Count);
    }

    [Fact]
    public void Captured_previous_device_takes_priority_over_configured_fallback()
    {
        var policy = Policy("monitor");
        policy.Observe(Snapshot("speakers"));
        policy.Observe(Snapshot("pods"));
        Assert.All(policy.Observe(Snapshot("monitor", false)), request => Assert.Equal("speakers", request.RestoreEndpointId));
    }

    [Fact]
    public void Unavailable_previous_output_is_not_replaced_with_a_guess()
    {
        var policy = Policy("monitor");
        policy.Observe(Snapshot("speakers"));
        policy.Observe(Snapshot("pods"));
        Assert.Empty(policy.Observe(Snapshot("monitor", false, speakersAvailable: false)));
    }

    [Fact]
    public void Already_restored_by_windows_needs_no_write()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers"));
        policy.Observe(Snapshot("pods"));
        Assert.Empty(policy.Observe(Snapshot("speakers", false)));
    }

    [Theory]
    [InlineData("pods")]
    [InlineData("pods-call")]
    public void Another_profile_of_the_same_device_is_not_a_valid_return_output(string fallback)
    {
        var policy = Policy(fallback);
        policy.Observe(Snapshot("pods"));
        Assert.False(policy.HasRememberedOutput);
        Assert.Empty(policy.Observe(Snapshot("monitor", false)));
    }

    [Fact]
    public void Disable_reconfiguration_and_sleep_forget_pending_restoration()
    {
        foreach (var reason in new[] { "disable", "reconfigure", "sleep" })
        {
            var policy = Policy();
            policy.Observe(Snapshot("speakers"));
            policy.Observe(Snapshot("pods"));
            if (reason == "disable") policy.Configure(false, "pods", Group, null);
            else if (reason == "reconfigure") policy.Configure(true, "other-pods", null, null);
            else policy.Forget();
            Assert.Empty(policy.Observe(Snapshot("monitor", false)));
        }
    }

    [Fact]
    public void Missing_role_reading_does_not_consume_that_roles_restore_claim()
    {
        var policy = Policy();
        policy.Observe(Snapshot("speakers"));
        policy.Observe(Snapshot("pods"));
        var partial = Snapshot("monitor", false) with { Defaults = new Dictionary<AudioOutputRole, string?>() };
        Assert.Empty(policy.Observe(partial));
        Assert.Equal(3, policy.Observe(Snapshot("monitor", false)).Count);
    }

    [Fact]
    public void Unknown_container_does_not_group_unrelated_outputs()
    {
        var policy = new AudioOutputRestorationPolicy();
        policy.Configure(true, "pods", null, null);
        var before = Snapshot("speakers") with { Endpoints = Snapshot("speakers").Endpoints.Select(endpoint => endpoint with { ContainerId = null }).ToArray() };
        policy.Observe(before);
        policy.Observe(before with { Defaults = Roles.ToDictionary(role => role, _ => (string?)"pods") });
        var gone = Snapshot("monitor", false) with { Endpoints = Snapshot("monitor", false).Endpoints.Select(endpoint => endpoint with { ContainerId = null }).ToArray() };
        Assert.Equal(3, policy.Observe(gone).Count);
    }

    [Theory]
    [InlineData("00000000-0000-0000-ffff-ffffffffffff")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public void Machine_or_invalid_container_ids_do_not_group_sibling_outputs(string containerId)
    {
        var policy = new AudioOutputRestorationPolicy();
        policy.Configure(true, "pods", containerId, null);
        var endpoints = new AudioOutputEndpoint[]
        {
            new("speakers", "Speakers", true, null),
            new("machine-output", "Machine output", true, containerId),
        };
        var initial = new AudioOutputSnapshot(endpoints,
            Roles.ToDictionary(role => role, _ => (string?)"speakers"));
        policy.Observe(initial);

        var machineOutputDefault = initial with
        {
            Defaults = Roles.ToDictionary(role => role, _ => (string?)"machine-output"),
        };
        policy.Observe(machineOutputDefault);

        Assert.False(policy.HasRememberedOutput);
    }

    [Fact]
    public void Coordinator_uses_controller_guard_and_ignores_queued_events_after_disable_or_dispose()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var settings = new HubSettings { RestorePreviousAudioOutput = true, AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        var queue = new Queue<Action>();
        using var vm = new AudioOutputViewModel(controller, settings, queue.Enqueue);
        controller.Snapshot = Snapshot("pods");
        controller.Notify(); queue.Dequeue()();
        controller.Snapshot = Snapshot("monitor", false);
        controller.Notify();
        settings.RestorePreviousAudioOutput = false;
        queue.Dequeue()();
        Assert.Empty(controller.Writes);
        settings.RestorePreviousAudioOutput = true;
        controller.Notify();
        vm.Dispose();
        queue.Dequeue()();
        Assert.Empty(controller.Writes);
        Assert.True(controller.Disposed);
    }

    [Fact]
    public void Dispose_reports_controller_cleanup_failure_and_ignores_queued_changes()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers"), FailDispose = true };
        var settings = new HubSettings { RestorePreviousAudioOutput = true, AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        var queue = new Queue<Action>();
        var vm = new AudioOutputViewModel(controller, settings, queue.Enqueue);
        controller.Snapshot = Snapshot("pods"); controller.Notify(); queue.Dequeue()();
        controller.Snapshot = Snapshot("monitor", false); controller.Notify();

        var exception = Record.Exception(vm.Dispose);

        Assert.Null(exception);
        Assert.Equal(1, controller.DisposeCalls);
        Assert.True(controller.Disposed);
        Assert.Contains("cleaning up", vm.StatusText);
        controller.Notify(); // the failed controller cleanup must not keep the VM subscribed
        Assert.Single(queue); // only the Changed callback queued before disposal remains
        queue.Dequeue()();
        Assert.Empty(controller.Writes);
    }

    [Fact]
    public void Saved_target_and_fallback_stay_selectable_when_windows_removes_the_endpoints()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var settings = new HubSettings
        {
            AudioTargetEndpointId = "pods",
            AudioTargetContainerId = Group,
            AudioFallbackEndpointId = "speakers",
        };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());

        controller.Snapshot = Snapshot("monitor") with
        {
            Endpoints = Snapshot("monitor").Endpoints.Where(endpoint => endpoint.Id == "monitor").ToArray(),
        };
        controller.Notify();

        Assert.False(Assert.IsType<AudioOutputChoice>(vm.SelectedTarget).IsAvailable);
        Assert.Contains("disconnected", vm.SelectedTarget!.Label, StringComparison.OrdinalIgnoreCase);
        Assert.False(Assert.IsType<AudioOutputChoice>(vm.SelectedFallback).IsAvailable);
        Assert.Contains("disconnected", vm.SelectedFallback!.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disconnected", vm.SelectedTargetStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Output_picker_hides_unselected_inactive_endpoints_and_sorts_active_outputs_first()
    {
        var controller = new FakeOutputs
        {
            Snapshot = new AudioOutputSnapshot(
            [
                new("pods", "AirPods stereo", false, Group),
                new("return", "Return headset", false, null),
                new("speakers", "Speakers", true, null),
                new("monitor", "Monitor", true, null),
                new("phantom", "Unnamed audio output", false, null),
                new("old-dock", "Old dock", false, null),
            ],
            Roles.ToDictionary(role => role, _ => (string?)"speakers")),
        };
        var settings = new HubSettings
        {
            AudioTargetEndpointId = "pods",
            AudioTargetContainerId = Group,
            AudioFallbackEndpointId = "return",
        };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());

        Assert.Equal(4, vm.Outputs.Count);
        Assert.All(vm.Outputs.Take(2), output => Assert.True(output.IsAvailable));
        Assert.Contains(vm.Outputs, output => output.Id == "pods" && !output.IsAvailable);
        Assert.Contains(vm.Outputs, output => output.Id == "return" && !output.IsAvailable);
        Assert.DoesNotContain(vm.Outputs, output => output.Id == "phantom");
        Assert.DoesNotContain(vm.Outputs, output => output.Id == "old-dock");
        Assert.Equal(6, controller.Snapshot.Endpoints.Count); // full Windows snapshot remains intact for policy
    }

    [Fact]
    public void Explicit_output_and_volume_commands_are_the_only_controller_writes()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        controller.Volumes["pods"] = 0.4f;
        var settings = new HubSettings { AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());

        Assert.Empty(controller.DefaultOutputWrites);
        Assert.Empty(controller.VolumeWrites);
        Assert.Equal(0.4f, vm.SelectedOutputVolume);

        vm.UseOutput("pods");
        vm.SetOutputVolume("pods", 0.4f); // Same as the loaded value: a binding echo must not write.
        Assert.Equal("pods", Assert.Single(controller.DefaultOutputWrites));
        Assert.Empty(controller.VolumeWrites);

        vm.SetOutputVolume("pods", 0.75f);
        Assert.Equal("pods", Assert.Single(controller.VolumeWrites));
        Assert.Equal(0.75f, vm.SelectedOutputVolume);

        vm.SetOutputVolume("speakers", 0.2f); // A stale control for another endpoint cannot write.
        vm.SetOutputVolume("pods", float.NaN);
        Assert.Single(controller.VolumeWrites);
    }

    [Fact]
    public void Disconnected_target_and_out_of_range_volume_never_trigger_writes()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers", airPodsAvailable: false) };
        var settings = new HubSettings { AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());

        vm.UseOutput("pods");
        vm.SetOutputVolume("pods", 0.8f);

        Assert.Empty(controller.DefaultOutputWrites);
        Assert.Empty(controller.VolumeWrites);
        Assert.Null(vm.SelectedOutputVolume);
    }

    [Fact]
    public void Current_windows_volume_tracks_the_default_independently_of_the_saved_airpods_target()
    {
        var controller = new FakeOutputs
        {
            Snapshot = new AudioOutputSnapshot(
            [
                new("pods", "AirPods stereo", false, Group),
                new("jbl", "JBL Flip", true, null),
            ], Roles.ToDictionary(role => role, _ => (string?)"jbl")),
        };
        controller.Volumes["jbl"] = 0.58f;
        controller.Volumes["pods"] = 0.31f;
        var settings = new HubSettings
        {
            RestorePreviousAudioOutput = true,
            AudioTargetEndpointId = "pods",
            AudioTargetContainerId = Group,
        };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("jbl", vm.CurrentWindowsOutputId);
        Assert.Equal("JBL Flip", vm.CurrentWindowsOutputName);
        Assert.Equal(0.58f, vm.CurrentWindowsOutputVolume);
        Assert.True(vm.CanAdjustCurrentOutput);
        Assert.Null(vm.SelectedOutputVolume); // the saved AirPods endpoint is currently disconnected
        Assert.Empty(controller.VolumeWrites); // reading the flyout value never writes volume
        Assert.Empty(controller.DefaultOutputWrites);
        Assert.Empty(controller.Writes);

        controller.Snapshot = new AudioOutputSnapshot(
        [
            new("pods", "AirPods stereo", true, Group),
            new("jbl", "JBL Flip", true, null),
        ], Roles.ToDictionary(role => role, _ => (string?)"pods"));
        controller.Notify();

        Assert.Equal("pods", vm.CurrentWindowsOutputId);
        Assert.Equal("AirPods stereo", vm.CurrentWindowsOutputName);
        Assert.Equal(0.31f, vm.CurrentWindowsOutputVolume);
        Assert.True(changed.Contains(nameof(vm.CurrentWindowsOutputId)));
        Assert.True(changed.Contains(nameof(vm.CurrentWindowsOutputGeneration)));
        Assert.True(changed.Contains(nameof(vm.CurrentWindowsOutputName)));
        Assert.True(changed.Contains(nameof(vm.CurrentWindowsOutputVolume)));
        Assert.Empty(controller.VolumeWrites);
        Assert.Empty(controller.DefaultOutputWrites);
        Assert.Empty(controller.Writes);

        vm.SetCurrentOutputVolume("pods", vm.CurrentWindowsOutputGeneration, 0.44f);
        Assert.Equal("pods", Assert.Single(controller.VolumeWrites));
        Assert.Equal(0.44f, vm.CurrentWindowsOutputVolume);
        Assert.Equal(0.44f, vm.SelectedOutputVolume); // both properties refer to the same endpoint
    }

    [Fact]
    public void Narrow_current_output_refresh_updates_volume_without_running_restoration_policy()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        controller.Volumes["speakers"] = 0.2f;
        var settings = new HubSettings
        {
            RestorePreviousAudioOutput = true,
            AudioTargetEndpointId = "pods",
            AudioTargetContainerId = Group,
        };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());

        controller.Snapshot = Snapshot("pods");
        controller.Notify(); // claim speakers as the return output
        Assert.Empty(controller.Writes);

        controller.Snapshot = Snapshot("monitor", airPodsAvailable: false);
        controller.Volumes["monitor"] = 0.61f;
        vm.RefreshCurrentWindowsOutput();

        Assert.Equal("monitor", vm.CurrentWindowsOutputId);
        Assert.Equal(0.61f, vm.CurrentWindowsOutputVolume);
        Assert.Empty(controller.Writes); // opening the flyout must not consume restoration intent
    }

    [Fact]
    public void Current_output_falls_back_to_console_when_multimedia_has_no_default()
    {
        var controller = new FakeOutputs
        {
            Snapshot = new AudioOutputSnapshot(
            [
                new("console", "Console headset", true, null),
                new("multimedia", "Multimedia speakers", true, null),
            ], new Dictionary<AudioOutputRole, string?> { [AudioOutputRole.Console] = "console" }),
        };
        using var vm = new AudioOutputViewModel(controller, new HubSettings(), action => action());

        Assert.Equal("console", vm.CurrentWindowsOutputId);
        Assert.Equal("Console headset", vm.CurrentWindowsOutputName);

        controller.Snapshot = controller.Snapshot with
        {
            Defaults = new Dictionary<AudioOutputRole, string?>
            {
                [AudioOutputRole.Multimedia] = null,
                [AudioOutputRole.Console] = "console",
            },
        };
        controller.Notify();

        Assert.Equal("console", vm.CurrentWindowsOutputId); // null means Windows has no Multimedia default
        Assert.Equal("Console headset", vm.CurrentWindowsOutputName);
        Assert.True(vm.CanAdjustCurrentOutput);

        controller.Snapshot = controller.Snapshot with
        {
            Defaults = new Dictionary<AudioOutputRole, string?>
            {
                [AudioOutputRole.Multimedia] = "",
                [AudioOutputRole.Console] = "console",
            },
        };
        controller.Notify();
        Assert.Equal("console", vm.CurrentWindowsOutputId); // blank is also treated as no Multimedia default

        controller.Snapshot = controller.Snapshot with
        {
            Defaults = new Dictionary<AudioOutputRole, string?>
            {
                [AudioOutputRole.Multimedia] = "multimedia",
                [AudioOutputRole.Console] = "console",
            },
        };
        controller.Notify();
        Assert.Equal("multimedia", vm.CurrentWindowsOutputId);
        Assert.Equal("Multimedia speakers", vm.CurrentWindowsOutputName);
    }

    [Fact]
    public void Current_output_volume_rejects_a_stale_default_and_an_observed_switch_away_and_back()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        controller.Volumes["speakers"] = 0.4f;
        controller.Volumes["pods"] = 0.2f;
        using var vm = new AudioOutputViewModel(controller, new HubSettings(), action => action());
        var capturedId = Assert.IsType<string>(vm.CurrentWindowsOutputId);
        var capturedGeneration = vm.CurrentWindowsOutputGeneration;

        // The OS snapshot may change before the delayed Changed callback reaches the VM.
        controller.Snapshot = Snapshot("pods");
        vm.SetCurrentOutputVolume(capturedId, capturedGeneration, 0.8f);
        Assert.Equal("pods", vm.CurrentWindowsOutputId);
        Assert.Empty(controller.VolumeWrites);
        Assert.Contains("changed", vm.CurrentWindowsOutputVolumeStatusText, StringComparison.OrdinalIgnoreCase);

        // Once a later refresh observes both transitions, an older drag remains retired even
        // when Windows has returned to the same endpoint ID.
        var generationAfterSwitch = vm.CurrentWindowsOutputGeneration;
        controller.Snapshot = Snapshot("speakers");
        controller.Notify();
        Assert.True(vm.CurrentWindowsOutputGeneration > generationAfterSwitch);
        vm.SetCurrentOutputVolume(capturedId, capturedGeneration, 0.7f);
        Assert.Empty(controller.VolumeWrites);
    }

    [Fact]
    public void Current_output_volume_is_disabled_for_an_unavailable_default_and_readback_is_sanitized()
    {
        var controller = new FakeOutputs
        {
            Snapshot = Snapshot("speakers", speakersAvailable: false),
        };
        using var vm = new AudioOutputViewModel(controller, new HubSettings(), action => action());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("speakers", vm.CurrentWindowsOutputId);
        Assert.False(vm.CanAdjustCurrentOutput);
        Assert.Null(vm.CurrentWindowsOutputVolume);
        vm.SetCurrentOutputVolume("speakers", vm.CurrentWindowsOutputGeneration, 0.8f);
        Assert.Empty(controller.VolumeWrites);

        controller.Snapshot = Snapshot("speakers");
        controller.Volumes["speakers"] = 1.5f;
        controller.Notify();
        Assert.Equal(1f, vm.CurrentWindowsOutputVolume); // native readback is clamped
        Assert.True(vm.CanAdjustCurrentOutput);

        controller.Volumes["speakers"] = float.NaN;
        controller.Notify();
        Assert.Null(vm.CurrentWindowsOutputVolume); // non-finite readback cannot feed a slider
        Assert.False(vm.CanAdjustCurrentOutput);
        Assert.Contains(nameof(vm.CurrentWindowsOutputVolume), changed);
        Assert.Contains(nameof(vm.CanAdjustCurrentOutput), changed);
        vm.SetCurrentOutputVolume("speakers", vm.CurrentWindowsOutputGeneration, 0.7f);
        Assert.Empty(controller.VolumeWrites);
    }

    [Fact]
    public void Coordinator_reports_native_refusal_with_role_and_endpoint_context()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var settings = new HubSettings { RestorePreviousAudioOutput = true, AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        var logs = new List<string>();
        using var vm = new AudioOutputViewModel(controller, settings, action => action(), logs.Add);
        controller.Snapshot = Snapshot("pods"); controller.Notify();
        controller.Snapshot = Snapshot("monitor", false); controller.Notify();
        Assert.Equal(3, controller.Writes.Count);
        Assert.Contains("newer choice preserved", vm.LastActionText);
        Assert.Equal(3, logs.Count);
        Assert.All(logs, line =>
        {
            Assert.Contains("expectedCurrent=" + LogText.OpaqueRef("monitor"), line);
            Assert.DoesNotContain("monitor", line);
            Assert.Contains("restoreTarget=" + LogText.OpaqueRef("speakers"), line);
            Assert.Contains("applied=False", line);
            Assert.DoesNotContain("newer choice preserved", line); // the log carries outcomes, not user-facing text
        });
        Assert.Contains(logs, line => line.Contains("role=Console", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("role=Multimedia", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("role=Communications", StringComparison.Ordinal));
    }

    [Fact]
    public void One_transient_read_failure_preserves_history_for_the_next_disconnect_snapshot()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var settings = new HubSettings { RestorePreviousAudioOutput = true, AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());
        controller.Snapshot = Snapshot("pods");
        controller.Notify();

        controller.FailRead = true; controller.Notify();
        controller.FailRead = false;
        controller.Snapshot = Snapshot("monitor", false); controller.Notify();

        Assert.Equal(3, controller.Writes.Count);
        Assert.All(controller.Writes, request => Assert.Equal("speakers", request.RestoreEndpointId));
    }

    [Fact]
    public void Reselecting_existing_target_from_settings_binding_preserves_captured_output()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var settings = new HubSettings { RestorePreviousAudioOutput = true, AudioTargetEndpointId = "pods", AudioTargetContainerId = Group, AudioFallbackEndpointId = "monitor" };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());
        controller.Snapshot = Snapshot("pods"); controller.Notify();
        vm.SelectTarget("pods"); // initial ComboBox SelectedItem binding, not a new user choice
        controller.Snapshot = Snapshot("monitor", false); controller.Notify();
        Assert.Equal(3, controller.Writes.Count);
        Assert.All(controller.Writes, request => Assert.Equal("speakers", request.RestoreEndpointId));
    }

    [Fact]
    public void Sleep_invalidation_wins_over_an_endpoint_refresh_already_queued()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var settings = new HubSettings { RestorePreviousAudioOutput = true, AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        var queue = new Queue<Action>();
        using var vm = new AudioOutputViewModel(controller, settings, queue.Enqueue);
        controller.Snapshot = Snapshot("pods"); controller.Notify(); queue.Dequeue()();
        controller.Snapshot = Snapshot("monitor", false); controller.Notify();
        vm.ForgetHistory(); // callback must invalidate before its UI cleanup reaches the queue
        while (queue.TryDequeue(out var action)) action();
        Assert.Empty(controller.Writes);
    }

    [Fact]
    public void Native_guard_rejects_a_request_invalidated_after_policy_evaluation()
    {
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var settings = new HubSettings { RestorePreviousAudioOutput = true, AudioTargetEndpointId = "pods", AudioTargetContainerId = Group };
        using var vm = new AudioOutputViewModel(controller, settings, action => action());
        controller.Snapshot = Snapshot("pods"); controller.Notify();
        controller.BeforeRestore = vm.ForgetHistory;
        controller.Snapshot = Snapshot("monitor", false); controller.Notify();
        Assert.Empty(controller.Writes);
    }

    [Fact]
    public void Changing_target_does_not_keep_the_previous_device_in_the_watched_group()
    {
        var policy = Policy();
        policy.Configure(true, "other-pods", "other-container", null);
        var first = Snapshot("speakers") with
        {
            Endpoints = [.. Snapshot("speakers").Endpoints, new("other-pods", "Other AirPods", true, "other-container")],
        };
        policy.Observe(first);
        policy.Observe(first with { Defaults = Roles.ToDictionary(role => role, _ => (string?)"other-pods") });
        var gone = first with
        {
            Endpoints = first.Endpoints.Select(endpoint => endpoint.Id == "other-pods" ? endpoint with { IsAvailable = false } : endpoint).ToArray(),
            Defaults = Roles.ToDictionary(role => role, _ => (string?)"monitor"),
        };
        Assert.Equal(3, policy.Observe(gone).Count); // original 'pods' is still active
    }

    [Fact]
    public void Audio_settings_round_trip_and_older_files_leave_the_feature_off()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClaspAudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Assert.False(new HubSettings().RestorePreviousAudioOutput);
            Assert.True(HubSettingsStore.Save(new HubSettings { RestorePreviousAudioOutput = true,
                AudioTargetEndpointId = "pods", AudioTargetContainerId = Group, AudioFallbackEndpointId = "speakers" }, path));
            var loaded = HubSettingsStore.Load(path);
            Assert.True(loaded.RestorePreviousAudioOutput);
            Assert.Equal("pods", loaded.AudioTargetEndpointId);
            Assert.Equal(Group, loaded.AudioTargetContainerId);
            Assert.Equal("speakers", loaded.AudioFallbackEndpointId);
            File.WriteAllText(path, "{\"Theme\":0,\"ReduceMotion\":false,\"ReduceTransparency\":false,\"AutoShowCaseCard\":true,\"CaseCardSeconds\":6}");
            Assert.False(HubSettingsStore.Load(path).RestorePreviousAudioOutput);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FakeOutputs : IAudioOutputController
    {
        public required AudioOutputSnapshot Snapshot { get; set; }
        public bool FailRead { get; set; }
        public bool FailDispose { get; set; }
        public bool Disposed { get; private set; }
        public int DisposeCalls { get; private set; }
        public Action? BeforeRestore { get; set; }
        public List<AudioOutputRestoreRequest> Writes { get; } = new();
        public List<string> DefaultOutputWrites { get; } = new();
        public List<string> VolumeWrites { get; } = new();
        public Dictionary<string, float> Volumes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public event EventHandler? Changed;
        public AudioOutputSnapshot Read() => FailRead ? throw new InvalidOperationException("unavailable") : Snapshot;
        public AudioOutputWriteResult TryRestore(AudioOutputRestoreRequest request)
        {
            BeforeRestore?.Invoke();
            if (request.IsCurrent?.Invoke() == false) return new(false, "request retired");
            Writes.Add(request);
            return new(false, "newer choice preserved");
        }
        public AudioOutputWriteResult TrySetDefaultOutput(string endpointId)
        {
            DefaultOutputWrites.Add(endpointId);
            return new(true, "default set");
        }
        public float? ReadVolume(string endpointId)
            => Snapshot.Endpoints.Any(endpoint => endpoint.Id == endpointId && endpoint.IsAvailable)
                ? Volumes.GetValueOrDefault(endpointId) : null;
        public AudioOutputWriteResult TrySetVolume(string endpointId, float scalar)
        {
            VolumeWrites.Add(endpointId);
            Volumes[endpointId] = scalar;
            return new(true, "volume set");
        }
        public void Notify() => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose()
        {
            DisposeCalls++;
            Disposed = true;
            if (FailDispose) throw new InvalidOperationException("unregister failed");
        }
    }
}
