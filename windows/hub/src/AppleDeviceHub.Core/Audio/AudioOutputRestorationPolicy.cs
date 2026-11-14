namespace AppleDeviceHub.Core.Audio;

/// <summary>
/// Remembers each Windows output role before the explicitly selected device becomes default.
/// Windows endpoint availability, never BLE/AAP proximity, determines disconnection. History
/// is session-only; changing configuration, disabling, sleep or disposal forgets ownership.
/// </summary>
public sealed class AudioOutputRestorationPolicy
{
    private static readonly Guid MachineContainerId = Guid.Parse("00000000-0000-0000-ffff-ffffffffffff");
    private readonly Dictionary<AudioOutputRole, string> _previousOutputs = new();
    private readonly Dictionary<AudioOutputRole, RestorationClaim> _claims = new();
    private readonly HashSet<string> _knownTargets = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;
    private string _targetId = "";
    private string? _containerId;
    private string? _fallbackId;

    public bool HasRememberedOutput => _claims.Count > 0;

    /// <summary>
    /// The output that would be restored for <paramref name="role"/> when the target goes away, or
    /// null when nothing is remembered. Read-only: the clasp://output/previous command uses it.
    /// </summary>
    public string? RestoreDestination(AudioOutputRole role)
        => _claims.TryGetValue(role, out var claim) ? claim.DestinationId : null;

    public void Configure(bool enabled, string targetId, string? containerId, string? fallbackId)
    {
        _enabled = enabled;
        _targetId = targetId;
        _containerId = NormalizeContainerId(containerId);
        _fallbackId = string.IsNullOrWhiteSpace(fallbackId) ? null : fallbackId;
        Forget();
    }

    public void Forget()
    {
        _previousOutputs.Clear();
        _claims.Clear();
        _knownTargets.Clear();
        if (!string.IsNullOrWhiteSpace(_targetId)) _knownTargets.Add(_targetId);
    }

    public IReadOnlyList<AudioOutputRestoreRequest> Observe(AudioOutputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_enabled || string.IsNullOrWhiteSpace(_targetId)) return [];
        foreach (var endpoint in snapshot.Endpoints)
            if (Same(endpoint.Id, _targetId) || SameContainer(endpoint.ContainerId, _containerId))
                _knownTargets.Add(endpoint.Id);

        bool targetAvailable = snapshot.Endpoints.Any(endpoint => IsTarget(endpoint.Id) && endpoint.IsAvailable);
        var requests = new List<AudioOutputRestoreRequest>();
        foreach (var role in Enum.GetValues<AudioOutputRole>())
        {
            // A missing role is an incomplete reading, not proof that it has no default.
            if (!snapshot.Defaults.TryGetValue(role, out var current)) continue;
            if (!targetAvailable)
            {
                if (_claims.Remove(role, out var claim) && !Same(current, claim.DestinationId)
                    && AvailableDestination(snapshot, claim.DestinationId))
                    requests.Add(new(role, claim.DestinationId, current, _knownTargets.ToArray(), _containerId));
            }
            else if (IsTarget(current))
            {
                // A stale default ID for a disappearing target profile cannot take
                // ownership away from the last profile Windows still reports available.
                if (AvailableEndpoint(snapshot, current))
                {
                    if (_claims.TryGetValue(role, out var claim))
                    {
                        // A Windows profile transition within the explicitly selected device
                        // changes which endpoint must still be available to treat a later
                        // default change as a manual choice.
                        if (!Same(claim.SourceEndpointId, current))
                            _claims[role] = claim with { SourceEndpointId = current! };
                    }
                    else
                    {
                        string? restore = _previousOutputs.GetValueOrDefault(role);
                        if (!AvailableDestination(snapshot, restore)) restore = _fallbackId;
                        if (restore is not null && AvailableDestination(snapshot, restore))
                            _claims[role] = new(restore, current!);
                    }
                }
            }
            else
            {
                // A choice away from a still-active source endpoint is treated as manual.
                // When Windows auto-falls from one disappearing profile to another active
                // profile in the same container, keep the history until the whole target is gone.
                if (_claims.TryGetValue(role, out var claim)
                    && snapshot.Endpoints.Any(endpoint => Same(endpoint.Id, claim.SourceEndpointId) && endpoint.IsAvailable))
                    _claims.Remove(role);
            }

            if (current is not null && AvailableDestination(snapshot, current)) _previousOutputs[role] = current;
        }
        return requests;
    }

    private bool IsTarget(string? id) => id is not null && _knownTargets.Contains(id);

    private static bool AvailableEndpoint(AudioOutputSnapshot snapshot, string? id)
        => id is not null && snapshot.Endpoints.Any(endpoint => Same(endpoint.Id, id) && endpoint.IsAvailable);

    private bool AvailableDestination(AudioOutputSnapshot snapshot, string? id)
        => id is not null && !IsTarget(id) && snapshot.Endpoints.Any(endpoint => Same(endpoint.Id, id) && endpoint.IsAvailable);

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameContainer(string? left, string? right)
        => NormalizeContainerId(left) is { } normalizedLeft
            && NormalizeContainerId(right) is { } normalizedRight
            && Same(normalizedLeft, normalizedRight);

    private static string? NormalizeContainerId(string? containerId)
        => Guid.TryParse(containerId, out var parsed)
            && parsed != Guid.Empty
            && parsed != MachineContainerId
                ? parsed.ToString("D")
                : null;

    private sealed record RestorationClaim(string DestinationId, string SourceEndpointId);
}
