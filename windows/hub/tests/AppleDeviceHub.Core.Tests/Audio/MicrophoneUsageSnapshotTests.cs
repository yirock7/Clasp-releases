using AppleDeviceHub.Core.Audio;

namespace AppleDeviceHub.Core.Tests.Audio;

public sealed class MicrophoneUsageSnapshotTests
{
    [Fact]
    public void A_failed_read_invalidates_prior_idle_state_until_a_successful_read_recovers()
    {
        var idle = MicrophoneUsageSnapshot.Unknown.AfterRead(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var activeApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "call-app.exe" };
        Assert.True(idle.IsReady);
        Assert.False(idle.IsMicrophoneInUse);

        var unknown = idle.AfterRead(null);

        Assert.False(unknown.IsReady);
        Assert.False(unknown.IsMicrophoneInUse);

        var recovered = unknown.AfterRead(activeApps);

        Assert.True(recovered.IsReady);
        Assert.True(recovered.IsMicrophoneInUse);
    }

    [Fact]
    public void A_captured_snapshot_keeps_readiness_and_usage_from_the_same_read_after_a_later_failure()
    {
        var idleSnapshot = MicrophoneUsageSnapshot.Unknown.AfterRead(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var capturedSnapshot = idleSnapshot;
        var latestSnapshot = idleSnapshot.AfterRead(null);

        Assert.True(capturedSnapshot.IsReady);
        Assert.False(capturedSnapshot.IsMicrophoneInUse);
        Assert.False(latestSnapshot.IsReady);
        Assert.False(latestSnapshot.IsMicrophoneInUse);
    }
}
