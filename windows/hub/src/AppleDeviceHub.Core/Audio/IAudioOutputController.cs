namespace AppleDeviceHub.Core.Audio;

public enum AudioOutputRole { Console = 0, Multimedia = 1, Communications = 2 }

/// <summary>A Windows render endpoint. ContainerId groups profiles of the explicitly selected device.</summary>
public sealed record AudioOutputEndpoint(string Id, string Name, bool IsAvailable, string? ContainerId);

public sealed record AudioOutputSnapshot(
    IReadOnlyList<AudioOutputEndpoint> Endpoints,
    IReadOnlyDictionary<AudioOutputRole, string?> Defaults);

/// <summary>A guarded restoration of one role, never a request to connect or select the AirPods.</summary>
public sealed record AudioOutputRestoreRequest(
    AudioOutputRole Role, string RestoreEndpointId, string? ExpectedCurrentEndpointId,
    IReadOnlyList<string> TargetEndpointIds, string? TargetContainerId, Func<bool>? IsCurrent = null);

public sealed record AudioOutputWriteResult(bool Applied, string Detail);

/// <summary>
/// Windows output endpoints only, independent of BLE/AAP. Use Read/TryRestore on the owning
/// UI thread; Changed may arrive on any thread and must be dispatched before reading.
/// TryRestore rechecks current default, target absence and destination availability immediately
/// before writing. No microphone changes, pairing, scanning or media playback commands.
/// </summary>
public interface IAudioOutputController : IDisposable
{
    event EventHandler? Changed;
    AudioOutputSnapshot Read();
    AudioOutputWriteResult TryRestore(AudioOutputRestoreRequest request);
    AudioOutputWriteResult TrySetDefaultOutput(string endpointId);
    float? ReadVolume(string endpointId);
    AudioOutputWriteResult TrySetVolume(string endpointId, float scalar);
}
