namespace AppleDeviceHub.Core.Audio;

/// <summary>
/// The last microphone-use registry read. A missing result means the current microphone-use
/// state is unknown, so Smart mode must wait for another successful read before acting.
/// </summary>
public sealed class MicrophoneUsageSnapshot
{
    private readonly HashSet<string> _appsInUse;

    private MicrophoneUsageSnapshot(bool isReady, IEnumerable<string> appsInUse)
    {
        IsReady = isReady;
        _appsInUse = new HashSet<string>(appsInUse, StringComparer.OrdinalIgnoreCase);
    }

    public static MicrophoneUsageSnapshot Unknown { get; } = new(false, []);

    public bool IsReady { get; }
    public IReadOnlySet<string> AppsInUse => _appsInUse;
    public bool IsMicrophoneInUse => IsReady && _appsInUse.Count > 0;

    /// <summary>Applies one complete read, or marks the snapshot unknown when the read failed.</summary>
    public MicrophoneUsageSnapshot AfterRead(IReadOnlySet<string>? appsInUse)
        => appsInUse is null ? new MicrophoneUsageSnapshot(false, _appsInUse) : new MicrophoneUsageSnapshot(true, appsInUse);
}
