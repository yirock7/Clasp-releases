namespace AppleDeviceHub.Core.Updates;

/// <summary>
/// How often Clasp looks for a new release. <see cref="NotAsked"/> is the state before the user
/// has answered the one-time question; like <see cref="Manual"/> it never checks on its own.
/// Stored by name, so the order here can change without changing what a saved file means.
/// </summary>
public enum UpdateCheckFrequency
{
    NotAsked = 0,
    Manual = 1,
    Daily = 2,
    Weekly = 3,
    Monthly = 4,
}

/// <summary>When an automatic update check is due. Pure: the caller supplies the clock.</summary>
public static class UpdateSchedule
{
    /// <summary>The time between automatic checks, or null when the app never checks on its own.</summary>
    public static TimeSpan? Interval(UpdateCheckFrequency frequency) => frequency switch
    {
        UpdateCheckFrequency.Daily => TimeSpan.FromDays(1),
        UpdateCheckFrequency.Weekly => TimeSpan.FromDays(7),
        UpdateCheckFrequency.Monthly => TimeSpan.FromDays(30),
        _ => null,
    };

    /// <summary>
    /// True when an automatic check should run now: the frequency checks on its own and the last
    /// check is at least one interval old. A last check in the future (clock moved back) counts as due.
    /// </summary>
    public static bool IsDue(UpdateCheckFrequency frequency, DateTimeOffset? lastCheckedAt, DateTimeOffset now)
    {
        if (Interval(frequency) is not { } interval) return false;
        if (lastCheckedAt is not { } last) return true;
        return last > now || now - last >= interval;
    }
}
