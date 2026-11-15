using AppleDeviceHub.Core.Resources;
using System.Globalization;

namespace AppleDeviceHub.Core.Audio;

/// <summary>Where the phone-to-PC audio stream is in its life cycle.</summary>
public enum PhoneStreamingPhase
{
    Idle = 0,
    Connecting = 1,
    Streaming = 2,
    Failed = 3,
    Unavailable = 4,
}

/// <summary>
/// The result of one open attempt, with the Windows types removed. The App adapter maps
/// <c>AudioPlaybackConnectionOpenResultStatus</c> to these values.
/// </summary>
public enum PhoneOpenOutcome
{
    Accepted = 0,
    DeniedBySystem = 1,
    TimedOut = 2,
    Failed = 3,
}

/// <summary>A device Windows says can stream audio to this PC. The name may be blank.</summary>
public sealed record PhoneStreamingCandidate(string DeviceId, string? Name);

/// <summary>The outcome of listing devices. <see cref="Succeeded"/> is false when Windows could not list them.</summary>
public sealed record PhoneDeviceSearch(IReadOnlyList<PhoneStreamingCandidate> Candidates, bool Succeeded);

/// <summary>
/// What the settings page shows for phone audio. <see cref="Message"/> is plain language and can be
/// displayed as it is.
/// </summary>
public sealed record PhoneStreamingState(PhoneStreamingPhase Phase, string? DeviceName, string Message)
{
    /// <summary>True while a connection is being opened or is streaming. Connect is disabled then.</summary>
    public bool IsActive => Phase is PhoneStreamingPhase.Connecting or PhoneStreamingPhase.Streaming;
}

/// <summary>Plain-language states for phone audio. Every method is pure, so the wording is tested.</summary>
public static class PhoneStreamingStates
{
    private static string UnnamedPhone => Strings.Audio_Phone_UnnamedPhone;

    public static PhoneStreamingState Idle()
        => new(PhoneStreamingPhase.Idle, null, Strings.Audio_Phone_Idle);

    public static PhoneStreamingState NoPhonesFound()
        => new(PhoneStreamingPhase.Idle, null,
            Strings.Audio_Phone_NoPhonesFound);

    public static PhoneStreamingState Unavailable()
        => new(PhoneStreamingPhase.Unavailable, null,
            Strings.Audio_Phone_Unavailable);

    public static PhoneStreamingState NotSupportedByDevice(string? deviceName)
        => new(PhoneStreamingPhase.Failed, DisplayName(deviceName),
            string.Format(CultureInfo.CurrentCulture, Strings.Audio_Phone_NotSupportedByDevice, DisplayName(deviceName)));

    public static PhoneStreamingState Connecting(string? deviceName)
    {
        var name = DisplayName(deviceName);
        return new(PhoneStreamingPhase.Connecting, name, string.Format(CultureInfo.CurrentCulture, Strings.Audio_Phone_Connecting, name));
    }

    public static PhoneStreamingState Streaming(string? deviceName)
    {
        var name = DisplayName(deviceName);
        return new(PhoneStreamingPhase.Streaming, name, string.Format(CultureInfo.CurrentCulture, Strings.Audio_Phone_Streaming, name));
    }

    /// <summary>The phone or Windows ended a stream that was running. Not an error the user caused.</summary>
    public static PhoneStreamingState Ended(string? deviceName)
        => new(PhoneStreamingPhase.Idle, null,
            string.Format(CultureInfo.CurrentCulture, Strings.Audio_Phone_Ended, DisplayName(deviceName)));

    public static PhoneStreamingState Rejected(string? deviceName, PhoneOpenOutcome outcome)
    {
        var name = DisplayName(deviceName);
        var message = outcome switch
        {
            PhoneOpenOutcome.DeniedBySystem =>
                Strings.Audio_Phone_Rejected_DeniedBySystem,
            PhoneOpenOutcome.TimedOut =>
                Strings.Audio_Phone_Rejected_TimedOut,
            _ => Strings.Audio_Phone_Rejected_Other,
        };
        return new(PhoneStreamingPhase.Failed, name, message);
    }

    /// <summary>Connect is allowed only when nothing is active and a device is selected.</summary>
    public static bool CanConnect(PhoneStreamingState state, string? selectedDeviceId)
        => !state.IsActive && !string.IsNullOrWhiteSpace(selectedDeviceId);

    /// <summary>Trimmed device name, or a neutral phrase when Windows gave no usable name.</summary>
    public static string DisplayName(string? deviceName)
        => string.IsNullOrWhiteSpace(deviceName) ? UnnamedPhone : deviceName.Trim();
}

/// <summary>Pure rules for the device list shown in the phone picker.</summary>
public static class PhoneDeviceList
{
    /// <summary>
    /// Drops blank IDs, keeps the first entry for each ID (case-insensitive), and sorts by name
    /// then ID so the picker does not reorder between refreshes.
    /// </summary>
    public static IReadOnlyList<PhoneStreamingCandidate> Normalize(IEnumerable<PhoneStreamingCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<PhoneStreamingCandidate>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.DeviceId)) continue;
            if (!seen.Add(candidate.DeviceId.Trim())) continue;
            kept.Add(candidate with { DeviceId = candidate.DeviceId.Trim() });
        }
        kept.Sort((left, right) =>
        {
            var byName = string.Compare(PhoneStreamingStates.DisplayName(left.Name), PhoneStreamingStates.DisplayName(right.Name), StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(left.DeviceId, right.DeviceId);
        });
        return kept;
    }

    /// <summary>Keeps the previous selection when it is still listed; otherwise picks the first device, or null.</summary>
    public static string? ChooseSelection(IReadOnlyList<PhoneStreamingCandidate> candidates, string? previousDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(previousDeviceId))
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.DeviceId, previousDeviceId.Trim(), StringComparison.OrdinalIgnoreCase)) return candidate.DeviceId;
            }
        }
        return candidates.Count > 0 ? candidates[0].DeviceId : null;
    }
}
