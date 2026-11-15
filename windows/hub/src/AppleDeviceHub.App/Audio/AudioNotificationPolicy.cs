namespace AppleDeviceHub.App.Audio;

/// <summary>
/// Computes the remaining settle delay from monotonic elapsed times. Kept free of timers and
/// Windows APIs so the native controller's quiet-period and cap behavior can be tested with
/// fixed values.
/// </summary>
internal static class AudioNotificationSettlePolicy
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(2);

    internal static TimeSpan GetRemainingDelay(TimeSpan elapsedSinceFirst, TimeSpan elapsedSinceLast)
    {
        var quietRemaining = QuietPeriod - elapsedSinceLast;
        var capRemaining = MaximumWait - elapsedSinceFirst;
        if (quietRemaining <= TimeSpan.Zero || capRemaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return quietRemaining < capRemaining ? quietRemaining : capRemaining;
    }
}

/// <summary>Small state machine for a single quiet-period notification window.</summary>
internal sealed class AudioNotificationSettleWindow
{
    private bool _disposed;
    private bool _hasPending;
    private long _firstTimestamp;
    private long _lastTimestamp;

    internal bool Record(long timestamp)
    {
        if (_disposed) return false;
        if (!_hasPending)
        {
            _hasPending = true;
            _firstTimestamp = timestamp;
        }
        _lastTimestamp = timestamp;
        return true;
    }

    internal TimeSpan? GetRemainingDelay(long nowTimestamp, long frequency)
    {
        if (_disposed || !_hasPending) return null;
        var elapsedSinceFirst = Elapsed(_firstTimestamp, nowTimestamp, frequency);
        var elapsedSinceLast = Elapsed(_lastTimestamp, nowTimestamp, frequency);
        return AudioNotificationSettlePolicy.GetRemainingDelay(elapsedSinceFirst, elapsedSinceLast);
    }

    internal bool TryComplete(long nowTimestamp, long frequency, out TimeSpan remainingDelay)
    {
        var delay = GetRemainingDelay(nowTimestamp, frequency);
        if (delay is null)
        {
            remainingDelay = Timeout.InfiniteTimeSpan;
            return false;
        }
        remainingDelay = delay.Value;
        if (remainingDelay > TimeSpan.Zero) return false;
        _hasPending = false;
        _firstTimestamp = 0;
        _lastTimestamp = 0;
        return true;
    }

    internal void Dispose()
    {
        _disposed = true;
        _hasPending = false;
        _firstTimestamp = 0;
        _lastTimestamp = 0;
    }

    private static TimeSpan Elapsed(long start, long end, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        var ticks = Math.Max(0, end - start);
        return TimeSpan.FromSeconds((double)ticks / frequency);
    }
}

/// <summary>
/// Tracks native endpoint notifications without making unrelated microphone or speaker
/// metadata changes cancel a restoration. Topology and per-role default changes remain broad
/// guards; state/property changes are checked only for endpoint IDs relevant to the request.
/// </summary>
internal sealed class AudioEndpointChangeTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _endpointVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly long[] _defaultVersions;
    private long _topologyVersion;

    internal AudioEndpointChangeTracker(int roleCount)
    {
        if (roleCount <= 0) throw new ArgumentOutOfRangeException(nameof(roleCount));
        _defaultVersions = new long[roleCount];
    }

    internal void RecordEndpointChange(string? endpointId, bool changesTopology = false)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(endpointId))
                _endpointVersions[endpointId] = _endpointVersions.GetValueOrDefault(endpointId) + 1;
            if (changesTopology) _topologyVersion++;
        }
    }

    internal void RecordDefaultChange(int role)
    {
        if ((uint)role >= (uint)_defaultVersions.Length) return;
        lock (_gate) _defaultVersions[role]++;
    }

    internal AudioEndpointChangeSnapshot Capture(int role)
    {
        if ((uint)role >= (uint)_defaultVersions.Length) throw new ArgumentOutOfRangeException(nameof(role));
        lock (_gate)
            return new(role, _topologyVersion, _defaultVersions[role],
                new Dictionary<string, long>(_endpointVersions, StringComparer.OrdinalIgnoreCase));
    }

    internal bool IsUnchangedSince(AudioEndpointChangeSnapshot snapshot, IEnumerable<string> relevantEndpointIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(relevantEndpointIds);
        if ((uint)snapshot.Role >= (uint)_defaultVersions.Length) return false;
        lock (_gate)
        {
            if (_topologyVersion != snapshot.TopologyVersion
                || _defaultVersions[snapshot.Role] != snapshot.DefaultVersion) return false;
            foreach (var endpointId in relevantEndpointIds)
            {
                if (string.IsNullOrWhiteSpace(endpointId)) continue;
                var before = snapshot.EndpointVersions.GetValueOrDefault(endpointId);
                var current = _endpointVersions.GetValueOrDefault(endpointId);
                if (current != before) return false;
            }
            return true;
        }
    }
}

internal sealed record AudioEndpointChangeSnapshot(
    int Role,
    long TopologyVersion,
    long DefaultVersion,
    IReadOnlyDictionary<string, long> EndpointVersions);
