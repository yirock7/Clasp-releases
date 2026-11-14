using AppleDeviceHub.Core.Audio;
using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.Core.Media;

/// <summary>
/// One guarded default-output change. The caller must re-read the default right before writing and
/// skip the write if it no longer equals <see cref="ExpectedCurrentEndpointId"/>, then report the
/// outcome with <see cref="EarOutputSwitchPolicy.ReportResult"/>.
/// </summary>
public sealed record EarOutputSwitchRequest(
    AudioOutputRole Role, string EndpointId, string ExpectedCurrentEndpointId, bool SwitchBack);

/// <summary>
/// Optional "when both AirPods are out of my ears, play through this speaker instead" (MagicPods'
/// auto switch sound output), driven only by gated, stable broadcast ear evidence
/// (<see cref="BroadcastEarResult"/>). Off while the speaker id is empty.
/// <para>
/// Rules: it switches only a role whose default is currently the AirPods, only on a transition from
/// "worn" (a stable reading with a bud in an ear) to "both out"; it switches back only to the AirPods
/// when a bud goes back in AND the default is still the speaker Clasp chose. Any other default change
/// (a manual choice, the restore-previous-output logic, the AirPods disconnecting) drops Clasp's claim
/// so a manual choice always wins. Restore-previous-output (<see cref="AudioOutputRestorationPolicy"/>)
/// treats a change away from a still-active AirPods endpoint as manual and forgets its own claim, so
/// the two never fight; when Clasp switches back, restore-previous-output simply records the speaker
/// as the output to restore later. The Communications role is never touched.
/// </para>
/// Session-only memory. Not thread-safe: call from the thread that owns the audio controller.
/// </summary>
public sealed class EarOutputSwitchPolicy
{
    private static readonly Guid MachineContainerId = Guid.Parse("00000000-0000-0000-ffff-ffffffffffff");
    private static readonly AudioOutputRole[] Roles = [AudioOutputRole.Console, AudioOutputRole.Multimedia];

    private readonly Dictionary<AudioOutputRole, Claim> _claims = new();
    private readonly HashSet<string> _airPodsIds = new(StringComparer.OrdinalIgnoreCase);
    private string _speakerId = "";
    private string? _containerId;
    private bool _wasWorn;

    /// <summary>How long after Clasp's switch it may still switch back.</summary>
    public TimeSpan SwitchBackWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>True while Clasp holds a switch it may undo (diagnostics).</summary>
    public bool HasSwitched => _claims.Count > 0;

    /// <summary>
    /// <paramref name="speakerEndpointId"/> is the chosen endpoint ("" = feature off). The AirPods
    /// endpoints are named by id and/or by the container id of the selected device.
    /// </summary>
    public void Configure(string speakerEndpointId, IEnumerable<string>? airPodsEndpointIds, string? airPodsContainerId)
    {
        _speakerId = speakerEndpointId?.Trim() ?? "";
        _airPodsIds.Clear();
        if (airPodsEndpointIds is not null)
            foreach (var id in airPodsEndpointIds)
                if (!string.IsNullOrWhiteSpace(id)) _airPodsIds.Add(id);
        _containerId = Normalize(airPodsContainerId);
        Forget();
    }

    /// <summary>Forget everything (sleep, disconnect, setting off, engine restart). Never switches.</summary>
    public void Forget()
    {
        _claims.Clear();
        _wasWorn = false;
    }

    /// <summary>The caller wrote (or failed to write) a requested switch.</summary>
    public void ReportResult(AudioOutputRole role, bool applied)
    {
        if (!_claims.TryGetValue(role, out var claim)) return;
        if (applied) _claims[role] = claim with { Applied = true };
        else _claims.Remove(role);
    }

    /// <summary>
    /// A gate result for a broadcast ear reading plus the current Windows outputs. Returns the
    /// switches to attempt (usually none).
    /// </summary>
    public IReadOnlyList<EarOutputSwitchRequest> OnEar(BroadcastEarResult result, AudioOutputSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_speakerId.Length == 0) return [];
        Observe(snapshot);

        switch (result.Verdict)
        {
            case BroadcastEarVerdict.Rejected:
                _wasWorn = false; // evidence lost: the next "both out" needs a fresh worn baseline
                return [];
            case BroadcastEarVerdict.Pending:
                return [];
        }

        var reading = result.Reading!;
        if (reading.InEarCount == 0)
        {
            if (!_wasWorn) return [];
            _wasWorn = false;
            return SwitchOut(snapshot, now);
        }

        _wasWorn = true;
        return SwitchBack(snapshot, now);
    }

    /// <summary>
    /// Call when Windows outputs changed. Drops Clasp's claim for any role the user (or anything else)
    /// moved away from Clasp's speaker, or whose AirPods are gone. Never returns requests.
    /// </summary>
    public void Observe(AudioOutputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var role in _claims.Keys.ToArray())
        {
            var claim = _claims[role];
            if (!snapshot.Defaults.TryGetValue(role, out var current)) continue; // incomplete reading
            if (!AnyAirPodsAvailable(snapshot)) { _claims.Remove(role); continue; }
            if (Same(current, _speakerId)) { _claims[role] = claim with { Applied = true }; continue; }
            // Not on the speaker. Before the write is confirmed it may still be on the AirPods
            // (the request is in flight); anything else is someone else's choice.
            if (claim.Applied || !IsAirPods(snapshot, current)) _claims.Remove(role);
        }
    }

    private List<EarOutputSwitchRequest> SwitchOut(AudioOutputSnapshot snapshot, DateTimeOffset now)
    {
        var requests = new List<EarOutputSwitchRequest>();
        var speaker = snapshot.Endpoints.FirstOrDefault(e => Same(e.Id, _speakerId) && e.IsAvailable);
        if (speaker is null || IsAirPods(snapshot, speaker.Id)) return requests;
        foreach (var role in Roles)
        {
            if (_claims.ContainsKey(role)) continue;
            if (!snapshot.Defaults.TryGetValue(role, out var current) || current is null) continue;
            if (!IsAirPods(snapshot, current, requireAvailable: true)) continue; // only leave the AirPods
            _claims[role] = new Claim(current, now, Applied: false);
            requests.Add(new EarOutputSwitchRequest(role, speaker.Id, current, SwitchBack: false));
        }
        return requests;
    }

    private List<EarOutputSwitchRequest> SwitchBack(AudioOutputSnapshot snapshot, DateTimeOffset now)
    {
        var requests = new List<EarOutputSwitchRequest>();
        foreach (var role in _claims.Keys.ToArray())
        {
            var claim = _claims[role];
            _claims.Remove(role); // acts at most once per switch
            if (now - claim.At > SwitchBackWindow) continue;
            if (!snapshot.Defaults.TryGetValue(role, out var current) || !Same(current, _speakerId)) continue; // manual choice wins
            var target = snapshot.Endpoints.FirstOrDefault(e => e.IsAvailable && Same(e.Id, claim.AirPodsEndpointId))
                ?? snapshot.Endpoints.FirstOrDefault(e => e.IsAvailable && IsAirPods(snapshot, e.Id));
            if (target is null) continue; // AirPods gone: leave the speaker alone
            requests.Add(new EarOutputSwitchRequest(role, target.Id, _speakerId, SwitchBack: true));
        }
        return requests;
    }

    private bool AnyAirPodsAvailable(AudioOutputSnapshot snapshot)
        => snapshot.Endpoints.Any(e => e.IsAvailable && IsAirPods(snapshot, e.Id));

    private bool IsAirPods(AudioOutputSnapshot snapshot, string? id, bool requireAvailable = false)
    {
        if (id is null) return false;
        var endpoint = snapshot.Endpoints.FirstOrDefault(e => Same(e.Id, id));
        if (requireAvailable && endpoint is not { IsAvailable: true }) return false;
        if (_airPodsIds.Contains(id)) return true;
        return _containerId is not null && endpoint is not null && Normalize(endpoint.ContainerId) is { } c && Same(c, _containerId);
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? containerId)
        => Guid.TryParse(containerId, out var parsed) && parsed != Guid.Empty && parsed != MachineContainerId
            ? parsed.ToString("D")
            : null;

    private sealed record Claim(string AirPodsEndpointId, DateTimeOffset At, bool Applied);
}
