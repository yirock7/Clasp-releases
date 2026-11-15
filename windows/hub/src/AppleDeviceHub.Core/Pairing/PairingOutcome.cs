using System.Globalization;
using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Pairing;

/// <summary>How a pairing attempt ended.</summary>
public enum PairingOutcome
{
    Paired,
    AlreadyPaired,
    NeedsWindowsSettings,
    Rejected,
    TimedOut,
    NotFound,
    Canceled,
    Failed,
}

/// <summary>Short plain-English text for each pairing outcome.</summary>
public static class PairingMessages
{
    /// <summary>The message to show for an outcome. Empty for <see cref="PairingOutcome.Canceled"/>.</summary>
    public static string Describe(PairingOutcome outcome, string deviceName) => outcome switch
    {
        PairingOutcome.Paired => Format(Strings.Pairing_Outcome_Paired, deviceName),
        PairingOutcome.AlreadyPaired => Format(Strings.Pairing_Outcome_AlreadyPaired, deviceName),
        PairingOutcome.NeedsWindowsSettings => Format(Strings.Pairing_Outcome_NeedsWindowsSettings, deviceName),
        PairingOutcome.Rejected => Format(Strings.Pairing_Outcome_Rejected, deviceName),
        PairingOutcome.TimedOut => Format(Strings.Pairing_Outcome_TimedOut, deviceName),
        PairingOutcome.NotFound => Format(Strings.Pairing_Outcome_NotFound, deviceName),
        PairingOutcome.Canceled => "",
        _ => Format(Strings.Pairing_Outcome_Failed, deviceName),
    };

    private static string Format(string format, string deviceName) => string.Format(CultureInfo.CurrentCulture, format, deviceName);
}
