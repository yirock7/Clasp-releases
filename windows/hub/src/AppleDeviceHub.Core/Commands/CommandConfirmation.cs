using AppleDeviceHub.Core.Resources;
using System.Globalization;

namespace AppleDeviceHub.Core.Commands;

/// <summary>
/// Decides which commands must be confirmed by the user inside Clasp when they come from outside the
/// app (a clasp:// link on a web page, the pipe from a second launch, a command line). Commands
/// from Clasp's own hotkeys and buttons never go through this check.
/// </summary>
public static class CommandConfirmation
{
    /// <summary>
    /// True when an externally requested command connects the AirPods (which also moves the sound to
    /// them), changes sound, repairs the connection or controls media playback.
    /// </summary>
    public static bool RequiresConfirmation(ClaspCommand command) => command is
        ClaspCommand.Connect or ClaspCommand.OutputAirPods or ClaspCommand.OutputPrevious
        or ClaspCommand.FixConnection or ClaspCommand.PlayPause;

    /// <summary>
    /// The plain-language question. <paramref name="outputName"/> is the name of the sound output when known;
    /// a generic wording is used when it is null or blank.
    /// </summary>
    public static string PromptText(ClaspCommand command, string? outputName)
    {
        var name = string.IsNullOrWhiteSpace(outputName) ? null : outputName.Trim();
        return command switch
        {
            ClaspCommand.OutputAirPods => name is null
                ? Strings.Command_Confirm_OutputAirPodsUnnamed
                : string.Format(CultureInfo.CurrentCulture, Strings.Command_Confirm_OutputNamed, name),
            ClaspCommand.OutputPrevious => name is null
                ? Strings.Command_Confirm_OutputPreviousUnnamed
                : string.Format(CultureInfo.CurrentCulture, Strings.Command_Confirm_OutputNamed, name),
            ClaspCommand.Connect => Strings.Command_Confirm_Connect,
            ClaspCommand.PlayPause => Strings.Command_Confirm_PlayPause,
            ClaspCommand.FixConnection => Strings.Command_Confirm_FixConnection,
            _ => throw new ArgumentOutOfRangeException(nameof(command), "This command does not need confirmation."),
        };
    }
}
