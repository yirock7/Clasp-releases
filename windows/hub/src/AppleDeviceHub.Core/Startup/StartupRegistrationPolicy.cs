using AppleDeviceHub.Core.Resources;

namespace AppleDeviceHub.Core.Startup;

/// <summary>What the "start Clasp when I sign in" setting means for the user.</summary>
public enum StartupStatus
{
    /// <summary>No per-user Run entry exists.</summary>
    Off = 0,
    /// <summary>The Run entry exists and Windows has not disabled it.</summary>
    On = 1,
    /// <summary>The Run entry exists, but Task Manager's Startup apps switch has turned it off.</summary>
    OffInTaskManager = 2,
}

/// <summary>The outcome of an enable or disable request, with the sentence the settings page shows.</summary>
public sealed record StartupChangeResult(bool Succeeded, StartupStatus Status, string Message);

/// <summary>
/// Pure rules for the per-user startup entry. The App adapter owns the registry calls; this class
/// only builds the command line and decides what the registry values mean.
/// </summary>
public static class StartupRegistrationPolicy
{
    /// <summary>Value name under the Run key. The installer's [Registry] entry uses the same name.</summary>
    public const string ValueName = "Clasp";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Windows' record of the Task Manager switch for Run entries. It is read but never written here,
    /// so a user's choice in Task Manager is respected.
    /// </summary>
    public const string ApprovalKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const string TransportArgument = "--transport=ble";
    public const string StartupArgument = "--startup";

    /// <summary>
    /// Builds the Run value: the quoted executable path, then the arguments that start Clasp to the tray.
    /// </summary>
    public static string BuildCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("An executable path is required.", nameof(exePath));
        if (exePath.Contains('"', StringComparison.Ordinal)) throw new ArgumentException("The executable path must not contain quotation marks.", nameof(exePath));
        return $"\"{exePath.Trim()}\" {TransportArgument} {StartupArgument}";
    }

    /// <summary>
    /// Reads the first byte of the StartupApproved value. Odd values (0x03, 0x07) mean Task Manager
    /// disabled the entry; even values (0x02, 0x06) or no value mean it is allowed. This matches the
    /// values Windows writes in current builds. Treat it as an observed convention, not a documented contract.
    /// </summary>
    public static bool IsApprovedByUser(ReadOnlySpan<byte> approvalValue)
        => approvalValue.Length == 0 || (approvalValue[0] & 0x01) == 0;

    /// <summary>Combines "Run value present" and the approval bytes into one status.</summary>
    public static StartupStatus Evaluate(bool runValuePresent, ReadOnlySpan<byte> approvalValue)
    {
        if (!runValuePresent) return StartupStatus.Off;
        return IsApprovedByUser(approvalValue) ? StartupStatus.On : StartupStatus.OffInTaskManager;
    }
}

/// <summary>Plain-language messages for the startup setting.</summary>
public static class StartupMessages
{
    public static string Describe(StartupStatus status) => status switch
    {
        StartupStatus.On => Strings.Startup_Status_On,
        StartupStatus.OffInTaskManager => Strings.Startup_Status_OffInTaskManager,
        _ => Strings.Startup_Status_Off,
    };

    /// <summary>The result of a successful change. The status decides the wording, so an entry Task Manager has disabled is reported, not hidden.</summary>
    public static StartupChangeResult Succeeded(StartupStatus status) => new(true, status, Describe(status));

    public static StartupChangeResult Failed(StartupStatus previous) => new(false, previous,
        Strings.Startup_Status_ChangeFailed);
}
