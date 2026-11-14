namespace AppleDeviceHub.App.Connection;

/// <summary>Rate-limits repeat reads of missing Apple device-node metadata without caching a permanent miss.</summary>
internal sealed class AppleIdentityRetryCache(TimeProvider timeProvider, TimeSpan retryInterval)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ushort> _lastKnownModelByContainer = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns true when this container's next metadata read is still cooling down.</summary>
    internal bool TryBeginRead(string containerId)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var expired in _retryAfter.Where(entry => now >= entry.Value).Select(entry => entry.Key).ToArray())
                _retryAfter.Remove(expired);
            if (_retryAfter.TryGetValue(containerId, out var retryAfter) && now < retryAfter) return false;

            // Claim the retry window before the native query so overlapping snapshots cannot storm it.
            _retryAfter[containerId] = now + retryInterval;
            return true;
        }
    }

    /// <summary>Starts a fresh cooldown after a query completed without a usable Apple model marker.</summary>
    internal void RememberMissing(string containerId)
    {
        lock (_gate) _retryAfter[containerId] = timeProvider.GetUtcNow() + retryInterval;
    }

    /// <summary>Stores a model marker found for this exact paired-device container and clears its cooldown.</summary>
    internal void RememberFound(string containerId, ushort modelId)
    {
        lock (_gate)
        {
            _retryAfter.Remove(containerId);
            _lastKnownModelByContainer[containerId] = modelId;
        }
    }

    /// <summary>
    /// Returns the last known model for this same container during a transient miss or cooldown.
    /// Battery is deliberately null because the latest query did not establish a fresh value.
    /// </summary>
    internal (ushort? ModelId, int? BatteryPercent) TransientFallback(string containerId)
    {
        lock (_gate)
        {
            return (_lastKnownModelByContainer.TryGetValue(containerId, out var modelId) ? modelId : null, null);
        }
    }
}
