using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace AppleDeviceHub.Core.Tests.Presentation;

/// <summary>
/// After the default output changes (or its volume reads as unknown) the displayed volume is re-read
/// at about 0.5, 1, 2 and 4 seconds. The re-reads only update the display; they never write a volume.
/// </summary>
public sealed class AudioVolumeRereadTests
{
    private static readonly AudioOutputRole[] Roles = Enum.GetValues<AudioOutputRole>();

    private static AudioOutputSnapshot Snapshot(string? defaultId)
        => new(
            [
                new("speakers", "Speakers", true, null),
                new("pods", "AirPods stereo", true, "container"),
            ],
            Roles.ToDictionary(role => role, _ => defaultId));

    private static (AudioOutputViewModel Vm, FakeOutputs Controller, FakeTimeProvider Clock, List<string> Logs) Create(string? initialDefault = "speakers")
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var logs = new List<string>();
        var controller = new FakeOutputs { Snapshot = Snapshot(initialDefault) };
        var vm = new AudioOutputViewModel(controller, new HubSettings(), action => action(),
            timeProvider: clock, log: logs.Add);
        return (vm, controller, clock, logs);
    }

    [Fact]
    public void A_default_change_re_reads_at_half_one_two_and_four_seconds_and_catches_up()
    {
        var (vm, controller, clock, logs) = Create();
        controller.Volumes["speakers"] = 0.4f;
        vm.Refresh();
        Assert.Equal(0.4f, vm.CurrentWindowsOutputVolume);
        var baseline = controller.VolumeReads;

        // The AirPods become the default and Windows briefly reports 0 for them.
        controller.Snapshot = Snapshot("pods");
        controller.Volumes["pods"] = 0f;
        vm.Refresh();
        Assert.Equal(0f, vm.CurrentWindowsOutputVolume); // shown as read, not faked
        Assert.True(vm.CanAdjustCurrentOutput);
        var afterChange = controller.VolumeReads;
        Assert.True(afterChange > baseline);

        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(afterChange, controller.VolumeReads);
        controller.Volumes["pods"] = 0.55f; // Windows settles
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(0.55f, vm.CurrentWindowsOutputVolume);

        var reads = new List<int> { controller.VolumeReads };
        clock.Advance(TimeSpan.FromMilliseconds(500)); // 1 s
        reads.Add(controller.VolumeReads);
        clock.Advance(TimeSpan.FromSeconds(1)); // 2 s
        reads.Add(controller.VolumeReads);
        clock.Advance(TimeSpan.FromSeconds(2)); // 4 s
        reads.Add(controller.VolumeReads);
        Assert.Equal([reads[0], reads[0] + 1, reads[0] + 2, reads[0] + 3], reads);

        clock.Advance(TimeSpan.FromSeconds(30)); // the schedule is finished
        Assert.Equal(reads[3], controller.VolumeReads);

        Assert.Empty(controller.VolumeWrites);
        Assert.Contains(logs, line => line.StartsWith("Audio volume: default output changed; first read 0%", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("now 55%, was 0%", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("pods", StringComparison.OrdinalIgnoreCase));
        vm.Dispose();
    }

    [Fact]
    public void An_unknown_volume_keeps_the_slider_unadjustable_and_is_filled_in_by_a_later_re_read()
    {
        var (vm, controller, clock, logs) = Create();
        controller.Volumes["speakers"] = 0.4f;
        vm.Refresh();

        controller.Snapshot = Snapshot("pods"); // no volume available yet
        vm.Refresh();

        Assert.Null(vm.CurrentWindowsOutputVolume);
        Assert.False(vm.CanAdjustCurrentOutput);
        Assert.Contains(logs, line => line.Contains("first read unknown", StringComparison.Ordinal));

        controller.Volumes["pods"] = 0.3f;
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0.3f, vm.CurrentWindowsOutputVolume);
        Assert.True(vm.CanAdjustCurrentOutput);
        Assert.Empty(controller.VolumeWrites);
        vm.Dispose();
    }

    [Fact]
    public void A_null_read_without_an_output_change_also_schedules_re_reads()
    {
        var (vm, controller, clock, _) = Create();
        vm.Refresh(); // speakers default, volume unknown
        Assert.False(vm.CanAdjustCurrentOutput);
        controller.Volumes["speakers"] = 0.7f;

        clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(0.7f, vm.CurrentWindowsOutputVolume);
        Assert.True(vm.CanAdjustCurrentOutput);
        vm.Dispose();
    }

    [Fact]
    public void Another_change_restarts_the_schedule()
    {
        var (vm, controller, clock, _) = Create();
        controller.Volumes["speakers"] = 0.4f;
        controller.Volumes["pods"] = 0.5f;
        vm.Refresh();
        controller.Snapshot = Snapshot("pods");
        vm.Refresh();
        clock.Advance(TimeSpan.FromMilliseconds(400));
        controller.Snapshot = Snapshot("speakers");
        vm.Refresh(); // restarts: the next re-read is 500 ms from here, not 100 ms
        var reads = controller.VolumeReads;

        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(reads, controller.VolumeReads);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(controller.VolumeReads > reads);
        vm.Dispose();
    }

    [Fact]
    public void Re_reads_never_set_a_volume_or_change_the_default_output()
    {
        var (vm, controller, clock, _) = Create();
        vm.Refresh();
        controller.Snapshot = Snapshot("pods");
        controller.Volumes["pods"] = 0f;
        vm.Refresh();

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(controller.VolumeWrites);
        Assert.Empty(controller.DefaultOutputWrites);
        vm.Dispose();
    }

    [Fact]
    public void Disposing_cancels_the_pending_re_reads()
    {
        var (vm, controller, clock, _) = Create();
        vm.Refresh();
        controller.Snapshot = Snapshot("pods");
        vm.Refresh();
        var reads = controller.VolumeReads;
        vm.Dispose();

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(reads, controller.VolumeReads);
    }

    [Fact]
    public void A_re_read_queued_on_the_dispatcher_does_nothing_after_dispose()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var controller = new FakeOutputs { Snapshot = Snapshot("speakers") };
        var queue = new Queue<Action>();
        var vm = new AudioOutputViewModel(controller, new HubSettings(), queue.Enqueue, timeProvider: clock);
        vm.Refresh();
        clock.Advance(TimeSpan.FromMilliseconds(500)); // timer fires, work is queued but not run
        Assert.NotEmpty(queue);
        var reads = controller.VolumeReads;

        vm.Dispose();
        while (queue.Count > 0) queue.Dequeue()();

        Assert.Equal(reads, controller.VolumeReads);
    }

    [Fact]
    public void An_unknown_volume_never_looks_adjustable_and_a_set_attempt_is_refused_without_writing()
    {
        var (vm, controller, _, _) = Create();
        vm.Refresh();
        Assert.Null(vm.CurrentWindowsOutputVolume);
        Assert.False(vm.CanAdjustCurrentOutput);

        vm.SetCurrentOutputVolume("speakers", vm.CurrentWindowsOutputGeneration, 0.5f);

        Assert.Empty(controller.VolumeWrites);
        vm.Dispose();
    }

    private sealed class FakeOutputs : IAudioOutputController
    {
        public required AudioOutputSnapshot Snapshot { get; set; }
        public Dictionary<string, float> Volumes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> VolumeWrites { get; } = new();
        public List<string> DefaultOutputWrites { get; } = new();
        public int VolumeReads { get; private set; }
        public int Reads { get; private set; }

        public event EventHandler? Changed { add { } remove { } }

        public AudioOutputSnapshot Read() { Reads++; return Snapshot; }

        public float? ReadVolume(string endpointId)
        {
            VolumeReads++;
            return Volumes.TryGetValue(endpointId, out var volume) ? volume : null;
        }

        public AudioOutputWriteResult TryRestore(AudioOutputRestoreRequest request) => new(false, "unused");

        public AudioOutputWriteResult TrySetDefaultOutput(string endpointId)
        {
            DefaultOutputWrites.Add(endpointId);
            return new(true, "default set");
        }

        public AudioOutputWriteResult TrySetVolume(string endpointId, float scalar)
        {
            VolumeWrites.Add(endpointId);
            Volumes[endpointId] = scalar;
            return new(true, "volume set");
        }

        public void Dispose() { }
    }
}
