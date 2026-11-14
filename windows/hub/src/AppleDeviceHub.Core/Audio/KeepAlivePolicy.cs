namespace AppleDeviceHub.Core.Audio;

/// <summary>Why <see cref="KeepAlivePolicy"/> chose its decision.</summary>
public enum KeepAliveReason
{
    Running,
    Disabled,
    NotConnected,
    NoEndpoint,
    NotDefaultOutput,
    BackingOff,
    RetriesExhausted,
}

/// <summary>What the caller should do with the silent keep-alive stream.</summary>
public readonly record struct KeepAliveDecision(bool ShouldRun, KeepAliveReason Reason)
{
    /// <summary>When backing off, the earliest time a new Run decision can appear.</summary>
    public DateTimeOffset? RetryAt { get; init; }
}

/// <summary>
/// Decides whether the silent keep-alive stream should be running. It should run only while the
/// setting is on, the AirPods are connected, their render endpoint is known and Windows uses that
/// endpoint as the default output (keeping the link awake matters only when sound goes there).
/// After a failure the policy waits <see cref="RetryDelay"/> before allowing another start, and
/// gives up after <see cref="MaxRetriesPerConnection"/> retries until the AirPods reconnect.
/// Not thread-safe: call from one thread (the UI thread).
/// </summary>
public sealed class KeepAlivePolicy
{
    private readonly TimeProvider _time;
    private bool _wasConnected;
    private int _failures;
    private DateTimeOffset? _retryAt;

    public KeepAlivePolicy(TimeProvider? time = null, TimeSpan? retryDelay = null, int maxRetriesPerConnection = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetriesPerConnection);
        _time = time ?? TimeProvider.System;
        RetryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
        MaxRetriesPerConnection = maxRetriesPerConnection;
    }

    /// <summary>Wait after a failure before another start is allowed.</summary>
    public TimeSpan RetryDelay { get; }

    /// <summary>Failed restarts allowed per connection after the first failure.</summary>
    public int MaxRetriesPerConnection { get; }

    /// <summary>Failures recorded for the current connection.</summary>
    public int FailureCount => _failures;

    /// <summary>Records that starting or running the stream failed (for example the device was removed).</summary>
    public void ReportFailure()
    {
        _failures++;
        _retryAt = _time.GetUtcNow() + RetryDelay;
    }

    /// <summary>Forgets failures, for example when the user toggles the setting.</summary>
    public void Reset()
    {
        _failures = 0;
        _retryAt = null;
    }

    /// <summary>Evaluates the current situation. Call on every state change and on a slow timer while backing off.</summary>
    public KeepAliveDecision Evaluate(bool enabled, bool airPodsConnected, string? endpointId, string? defaultOutputEndpointId)
    {
        // A fresh connection gets a fresh retry budget.
        if (airPodsConnected && !_wasConnected) Reset();
        _wasConnected = airPodsConnected;

        if (!enabled) return new(false, KeepAliveReason.Disabled);
        if (!airPodsConnected) return new(false, KeepAliveReason.NotConnected);
        if (string.IsNullOrWhiteSpace(endpointId)) return new(false, KeepAliveReason.NoEndpoint);
        if (!string.Equals(endpointId, defaultOutputEndpointId, StringComparison.OrdinalIgnoreCase))
            return new(false, KeepAliveReason.NotDefaultOutput);
        if (_failures > MaxRetriesPerConnection) return new(false, KeepAliveReason.RetriesExhausted);
        if (_retryAt is { } at && _time.GetUtcNow() < at)
            return new(false, KeepAliveReason.BackingOff) { RetryAt = at };
        return new(true, KeepAliveReason.Running);
    }
}
