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
        PairingOutcome.Paired => $"Paired with {deviceName}.",
        PairingOutcome.AlreadyPaired => $"{deviceName} is already paired.",
        PairingOutcome.NeedsWindowsSettings => $"{deviceName} asked for a code. Pair it in Windows Bluetooth settings instead.",
        PairingOutcome.Rejected => $"{deviceName} didn't accept. Make sure the light is flashing white, then try again.",
        PairingOutcome.TimedOut => $"{deviceName} didn't answer in time. Keep the case open next to your PC and try again.",
        PairingOutcome.NotFound => $"{deviceName} is no longer in pairing mode. Double-tap or press the case button again.",
        PairingOutcome.Canceled => "",
        _ => $"Couldn't pair {deviceName}. Try again, or use Windows Bluetooth settings.",
    };
}
