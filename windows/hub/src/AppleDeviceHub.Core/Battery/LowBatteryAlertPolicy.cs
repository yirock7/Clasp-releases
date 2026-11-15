using System.Globalization;
using AppleDeviceHub.Core.Ble;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Battery;

/// <summary>What to tell the user. The percentage is the broadcast/driver value (broadcast is in 10 % steps).</summary>
public sealed record LowBatteryAlert(string Title, string Body, int Percent);

/// <summary>
/// Decides when to raise the low-battery notification. One alert when the lowest earbud falls to
/// the threshold or below; it is armed again only after the earbuds were seen charging and the
/// lowest level is above threshold + 10 and a monotonic cooldown (default 2 h) since the last alert has passed, so
/// neither charging flags nor level jumps can shorten it. A threshold of 0 turns alerts off. Only a latched identity
/// (<see cref="IdentityConfidence.LikelyYours"/>) may alert or re-arm: Unknown, Probable and battery-only matches
/// never alert and do not change any state. Not thread-safe.
/// </summary>
public sealed class LowBatteryAlertPolicy
{
    public const int Hysteresis = 10;

    private sealed class DeviceState
    {
        public bool Armed = true;
        public bool ChargedSinceAlert;
        public long AlertedAt;
    }

    /// <summary>Default minimum time between two alerts for one device.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromHours(2);

    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _cooldown;

    public LowBatteryAlertPolicy(TimeProvider? time = null, TimeSpan? cooldown = null)
    {
        _time = time ?? TimeProvider.System;
        _cooldown = cooldown ?? DefaultCooldown;
        if (_cooldown < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cooldown));
    }

    /// <param name="deviceId">The paired device the reading belongs to.</param>
    /// <param name="identity">How sure we are the reading is the user's AirPods.</param>
    /// <param name="left">Left earbud level, null when not reported.</param>
    /// <param name="right">Right earbud level, null when not reported.</param>
    /// <param name="podCharging">True when a reported earbud is charging.</param>
    /// <param name="thresholdPercent">0 = off.</param>
    /// <param name="layout">Which batteries the device has; a single battery reports no charging, so a rising level re-arms it.</param>
    /// <param name="noun">"AirPods", "Beats" or "headphones", for the title.</param>
    /// <returns>The alert to show, or null.</returns>
    public LowBatteryAlert? Evaluate(string deviceId, IdentityConfidence identity, int? left, int? right, bool podCharging, int thresholdPercent,
        BatteryLayout layout = BatteryLayout.EarbudsWithCase, string noun = "AirPods")
    {
        if (!identity.IsLatched()) return null; // a battery-only match is forgeable: it may not alert or re-arm
        int? lowest = (left, right) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => null,
        };
        if (lowest is not int level) return null;

        if (!_devices.TryGetValue(deviceId, out var state))
        {
            state = new DeviceState();
            _devices[deviceId] = state;
        }

        if (podCharging) state.ChargedSinceAlert = true;
        if (thresholdPercent <= 0) return null;
        if (!state.Armed)
        {
            // A single battery never reports charging, so the level rising is the only evidence it was charged.
            if ((state.ChargedSinceAlert || layout == BatteryLayout.SingleBattery) && level > thresholdPercent + Hysteresis
                && _time.GetElapsedTime(state.AlertedAt) >= _cooldown)
            {
                state.Armed = true;
                state.ChargedSinceAlert = false;
            }
            return null;
        }

        if (podCharging || level > thresholdPercent) return null;
        state.Armed = false;
        state.ChargedSinceAlert = false;
        state.AlertedAt = _time.GetTimestamp();
        return Describe(level, left, right, layout, noun);
    }

    /// <summary>Forget every device (for example when the paired device changes).</summary>
    public void Reset() => _devices.Clear();

    private static LowBatteryAlert Describe(int level, int? left, int? right, BatteryLayout layout, string noun)
    {
        string title = string.Format(CultureInfo.CurrentCulture, Strings.Battery_Alert_Title, noun);
        string pct = level.ToString(CultureInfo.InvariantCulture);
        if (layout == BatteryLayout.SingleBattery)
            return new LowBatteryAlert(title, string.Format(CultureInfo.CurrentCulture, Strings.Battery_Alert_Single, pct), level);
        bool chargeOnly = layout == BatteryLayout.EarbudsNoCaseBattery;
        string template = (left, right) switch
        {
            (int l, int r) when l == level && r == level => chargeOnly ? Strings.Battery_Alert_BothCharge : Strings.Battery_Alert_BothCase,
            (int l, _) when l == level => chargeOnly ? Strings.Battery_Alert_LeftCharge : Strings.Battery_Alert_LeftCase,
            _ => chargeOnly ? Strings.Battery_Alert_RightCharge : Strings.Battery_Alert_RightCase,
        };
        return new LowBatteryAlert(title, string.Format(CultureInfo.CurrentCulture, template, pct), level);
    }
}
