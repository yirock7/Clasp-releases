using AppleDeviceHub.Core.Contracts;

namespace AppleDeviceHub.Core.Presentation;

/// <summary>
/// Decides when the animated case card appears on its own. BUILD-PLAN section 4 asks for
/// "one presentation per meaningful event, rather than one for every Bluetooth advertisement".
/// The tray menu's "Show case card" bypasses this policy.
/// </summary>
public sealed class CaseCardPolicy
{
    /// <summary>User setting: show the card automatically at all.</summary>
    public bool AutoPresentEnabled { get; set; } = true;

    /// <summary>Minimum time between automatic presentations.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When the card was last shown automatically, or null.</summary>
    public DateTimeOffset? LastPresentedAt { get; private set; }

    /// <summary>
    /// Should this state change show the card? Called once per applied state, in order.
    /// <paramref name="previous"/> is null for the first state a transport reports, and for the
    /// first state after a switch to a different device. <see cref="IsEligible"/> has already
    /// suppressed disabled, unattributed (broadcast/unknown origin) and duplicate reports.
    /// </summary>
    public bool ShouldPresent(DeviceState? previous, DeviceState next, DateTimeOffset now)
    {
        if (!IsEligible(previous, next, out previous)) return false;

        // No transition rule is implemented yet, so this always returns false and the card
        // never appears on its own after the guards above pass. The tray menu still shows it.
        return false;
    }

    public void MarkPresented(DateTimeOffset now) => LastPresentedAt = now;

    /// <summary>
    /// Suppression and attribution guards applied before the transition rule. False means the
    /// card must not appear on its own for this state, whatever the transition:
    /// <list type="bullet">
    /// <item>the setting is off;</item>
    /// <item>the data is not attributed to the user's device: a nearby broadcast
    /// (<see cref="DataOrigin.Broadcast"/>, anyone's AirPods could send it) or an unknown origin;</item>
    /// <item>it is a duplicate or out-of-order report (sequence not newer).</item>
    /// </list>
    /// <paramref name="comparable"/> is the previous state the rule may compare against: null
    /// when there is none or it belonged to a different device, so a device switch is judged as
    /// a first report rather than as a transition between two devices.
    /// </summary>
    public bool IsEligible(DeviceState? previous, DeviceState next, out DeviceState? comparable)
    {
        comparable = previous is not null && previous.DeviceId == next.DeviceId ? previous : null;
        if (!AutoPresentEnabled) return false;
        if (next.Origin is DataOrigin.Broadcast or DataOrigin.Unknown) return false;
        if (comparable is not null && next.Sequence <= comparable.Sequence) return false;
        return true;
    }
}
