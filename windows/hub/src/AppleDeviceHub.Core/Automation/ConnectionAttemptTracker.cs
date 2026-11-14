namespace AppleDeviceHub.Core.Automation;

/// <summary>
/// Remembers where each connect attempt came from, and for how long that is allowed to matter.
/// The origin belongs to one attempt (it has an id), not to the clock: it ends with the attempt.
/// <list type="bullet">
/// <item>Every terminal outcome that did not produce a connection (Busy, Failed, Unsupported,
/// cancellation, an exception, a timeout) clears the origin through <see cref="Complete"/>.</item>
/// <item>An attempt that did produce the connection hands its origin to the connection-actions policy until
/// the host has used it (<see cref="Consume"/>), the short <see cref="ProducedConnectionWindow"/> runs out,
/// the device changes, or any later attempt begins.</item>
/// <item>A nearby (automatic) attempt is always <see cref="ConnectionOrigin.NearbyAuto"/>. It never inherits
/// a leftover origin; the only exception is an explicit attempt that is still running and owns the
/// connection, which keeps its own origin.</item>
/// </list>
/// Thread-safe: attempts finish on background threads while the host reads on the UI thread.
/// </summary>
public sealed class ConnectionAttemptTracker
{
    /// <summary>The id returned when no new attempt was started (an automatic one joined a running explicit one).</summary>
    public const long NoAttempt = 0;

    /// <summary>How long a finished attempt's origin stays usable after it produced the connection.</summary>
    public static readonly TimeSpan ProducedConnectionWindow = TimeSpan.FromSeconds(30);

    /// <summary>Safety net: an attempt that never reports an outcome stops counting after this long.</summary>
    public static readonly TimeSpan RunningAttemptLimit = TimeSpan.FromMinutes(2);

    private sealed record Attempt(long Id, ConnectionOrigin Origin, string? DeviceId, DateTimeOffset StartedAt);

    private readonly object _gate = new();
    private long _nextId;
    private Attempt? _running;
    private Attempt? _produced;
    private DateTimeOffset _producedAt;

    /// <summary>
    /// Starts an attempt and returns its id. An unknown or undefined origin is treated as the least trusted
    /// one (<see cref="ConnectionOrigin.NearbyAuto"/>). A nearby attempt starts as NearbyAuto and discards any
    /// leftover origin, unless an explicit attempt for the same device is still running: then the explicit
    /// attempt keeps its origin and <see cref="NoAttempt"/> is returned.
    /// </summary>
    public long Begin(ConnectionOrigin requested, string? deviceId, DateTimeOffset now)
    {
        var origin = requested is ConnectionOrigin.None || !Enum.IsDefined(requested) ? ConnectionOrigin.NearbyAuto : requested;
        lock (_gate)
        {
            if (origin == ConnectionOrigin.NearbyAuto
                && _running is { Origin: not ConnectionOrigin.NearbyAuto } running
                && now - running.StartedAt < RunningAttemptLimit
                && string.Equals(running.DeviceId, deviceId, StringComparison.Ordinal))
            {
                return NoAttempt;
            }
            _produced = null; // a new attempt never inherits an earlier one
            var id = ++_nextId;
            _running = new Attempt(id, origin, deviceId, now);
            return id;
        }
    }

    /// <summary>
    /// The attempt is over. If it produced the connection its origin is kept for the policy; every other
    /// outcome forgets it. A stale or unknown id (a superseded attempt) changes nothing.
    /// </summary>
    public void Complete(long attemptId, bool connected, DateTimeOffset now)
    {
        if (attemptId == NoAttempt) return;
        lock (_gate)
        {
            if (_running is not { } running || running.Id != attemptId) return;
            _running = null;
            if (connected)
            {
                _produced = running;
                _producedAt = now;
            }
            else
            {
                _produced = null;
            }
        }
    }

    /// <summary>
    /// The origin of the attempt that is producing, or has just produced, the connection of
    /// <paramref name="deviceId"/>; <see cref="ConnectionOrigin.None"/> when no Clasp attempt is responsible.
    /// </summary>
    public ConnectionOrigin Current(string? deviceId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_running is { } running)
            {
                if (now - running.StartedAt >= RunningAttemptLimit
                    || !string.Equals(running.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    _running = null;
                }
                else
                {
                    return running.Origin;
                }
            }
            if (_produced is { } produced)
            {
                if (now - _producedAt >= ProducedConnectionWindow
                    || !string.Equals(produced.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    _produced = null;
                }
                else
                {
                    return produced.Origin;
                }
            }
            return ConnectionOrigin.None;
        }
    }

    /// <summary>True while an attempt is in progress (used by tests and diagnostics).</summary>
    public bool HasRunningAttempt
    {
        get { lock (_gate) return _running is not null; }
    }

    /// <summary>The host has used the origin (the connection's actions are planned): forget everything.</summary>
    public void Consume() => Clear();

    /// <summary>Selection change, disconnect, sleep/resume, repair or shutdown: no origin survives.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _running = null;
            _produced = null;
        }
    }
}
