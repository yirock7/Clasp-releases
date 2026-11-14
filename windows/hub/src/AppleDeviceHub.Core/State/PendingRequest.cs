using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.State;

public enum RequestStatus
{
    /// <summary>Sent; waiting for the engine's answer and the device's readback.</summary>
    Pending = 0,

    /// <summary>The device reported the requested value after the request.</summary>
    Confirmed = 1,

    /// <summary>The engine rejected or could not deliver it. The display returns to the actual value.</summary>
    Failed = 2,

    /// <summary>No matching readback in time. Shown like Failed; the command might still land later.</summary>
    TimedOut = 3,

    /// <summary>
    /// The device changed to a different value on its own (earbud stem, iPhone) while this was
    /// pending. The other change wins: BUILD-PLAN section 7 says to preserve manual changes.
    /// </summary>
    Superseded = 4,
}

/// <summary>
/// A user request for a device setting, tracked from "requested" to an honest final status.
/// Generic so listening mode, Conversation Awareness and Adaptive strength share one set of rules.
/// </summary>
/// <param name="CommandId">Identity of the command sent for this request.</param>
/// <param name="Requested">The value the user asked for.</param>
/// <param name="ActualWhenRequested">The device's reported value when the request began (null: unknown).</param>
/// <param name="IssuedAt">When the request began; timeouts count from here.</param>
/// <param name="Status">Where the request stands now.</param>
/// <param name="Detail">Human-readable reason for a failure or timeout, if any.</param>
/// <param name="EarlierOwnValues">
/// Values of this app's own still-in-flight requests that this one replaced (rapid clicks).
/// A readback of one of them is our own earlier command landing, not an external change.
/// </param>
public sealed record PendingRequest<T>(
    Guid CommandId,
    T Requested,
    T? ActualWhenRequested,
    DateTimeOffset IssuedAt,
    RequestStatus Status,
    string? Detail = null,
    IReadOnlyList<T>? EarlierOwnValues = null)
    where T : struct
{
    public bool IsPending => Status == RequestStatus.Pending;
}

/// <summary>
/// Pure functions that move a <see cref="PendingRequest{T}"/> forward. Each input (command
/// result, device readback, clock tick) can arrive in any order; the rules below make the
/// outcome independent of that order.
/// </summary>
public static class RequestReconciler
{
    /// <summary>Start tracking a request.</summary>
    /// <param name="commandId">Identity of the command being sent.</param>
    /// <param name="requested">The value the user asked for.</param>
    /// <param name="actualNow">The device's current reported value (null: unknown).</param>
    /// <param name="now">The current time.</param>
    /// <param name="replacing">
    /// The previous request for the same setting, if any. When it is still pending, the new
    /// request inherits its baseline and remembers its value as "ours", so the older command
    /// landing first does not look like a change made on the device.
    /// </param>
    public static PendingRequest<T> Begin<T>(
        Guid commandId, T requested, T? actualNow, DateTimeOffset now, PendingRequest<T>? replacing = null)
        where T : struct
    {
        if (replacing is not { IsPending: true } older)
            return new(commandId, requested, actualNow, now, RequestStatus.Pending);

        var earlier = (older.EarlierOwnValues ?? Array.Empty<T>()).Append(older.Requested).Distinct().ToArray();
        return new(commandId, requested, older.ActualWhenRequested, now, RequestStatus.Pending, null, earlier);
    }

    /// <summary>Apply the engine's answer. Answers for older commands are ignored.</summary>
    public static PendingRequest<T> OnCommandResult<T>(PendingRequest<T> request, CommandResult result)
        where T : struct
    {
        if (request.CommandId != result.CommandId || !request.IsPending) return request;

        // Acknowledged only means "accepted for delivery": keep waiting for the readback.
        if (result.Outcome == CommandOutcome.Acknowledged) return request;

        return request with
        {
            Status = RequestStatus.Failed,
            Detail = string.IsNullOrWhiteSpace(result.Detail) ? Describe(result.Outcome) : result.Detail,
        };
    }

    /// <summary>Apply a new device report of the actual value.</summary>
    public static PendingRequest<T> OnActual<T>(PendingRequest<T> request, T? actual)
        where T : struct
    {
        if (actual is not T value) return request;

        // A readback that arrives after the timeout still proves the change landed.
        if (request.Status == RequestStatus.TimedOut && EqualityComparer<T>.Default.Equals(value, request.Requested))
            return request with { Status = RequestStatus.Confirmed, Detail = "Confirmed after the timeout" };

        if (!request.IsPending) return request;

        if (EqualityComparer<T>.Default.Equals(value, request.Requested))
            return request with { Status = RequestStatus.Confirmed, Detail = null };

        // One of our own replaced requests landing first: keep waiting for this one.
        if (request.EarlierOwnValues?.Contains(value) == true) return request;

        // A value that is neither the old one nor the requested one came from somewhere else.
        bool changedElsewhere = request.ActualWhenRequested is not T before
            || !EqualityComparer<T>.Default.Equals(value, before);
        return changedElsewhere
            ? request with { Status = RequestStatus.Superseded, Detail = "Changed on the device" }
            : request;
    }

    /// <summary>Time out a request that has waited too long for its readback.</summary>
    public static PendingRequest<T> OnTick<T>(PendingRequest<T> request, DateTimeOffset now, TimeSpan timeout)
        where T : struct
        => request.IsPending && now - request.IssuedAt >= timeout
            ? request with { Status = RequestStatus.TimedOut, Detail = "No confirmation from the device" }
            : request;

    private static string Describe(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Rejected => "The device rejected the change",
        CommandOutcome.Failed => "The change could not be delivered",
        CommandOutcome.TimedOut => "The engine did not answer in time",
        CommandOutcome.Unsupported => "Not supported by this device",
        CommandOutcome.TransportUnavailable => "The device engine is not running",
        _ => outcome.ToString(),
    };
}
