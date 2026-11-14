namespace AppleDeviceHub.Core.Commands;

/// <summary>
/// Things an outside trigger (a clasp:// link, a command-line argument or a global hotkey) may
/// ask the running app to do. Deliberately small and fixed: every one is something the user can
/// already do with a click in the flyout.
/// </summary>
public enum ClaspCommand
{
    Connect = 0,
    Disconnect = 1,
    OutputAirPods = 2,
    OutputPrevious = 3,
    FixConnection = 4,
    ShowFlyout = 5,
    ShowSettings = 6,
    PlayPause = 7,
}

/// <summary>Plain-language names for <see cref="ClaspCommand"/>.</summary>
public static class ClaspCommandText
{
    /// <summary>The short lowercase name used by <c>--command=</c> and the pipe protocol.</summary>
    public static string CanonicalName(ClaspCommand command) => command switch
    {
        ClaspCommand.Connect => "connect",
        ClaspCommand.Disconnect => "disconnect",
        ClaspCommand.OutputAirPods => "output-airpods",
        ClaspCommand.OutputPrevious => "output-previous",
        ClaspCommand.FixConnection => "fix",
        ClaspCommand.ShowFlyout => "show",
        ClaspCommand.ShowSettings => "settings",
        ClaspCommand.PlayPause => "playpause",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>The label shown next to a hotkey box in Settings.</summary>
    public static string DisplayName(ClaspCommand command) => command switch
    {
        ClaspCommand.Connect => "Connect AirPods",
        ClaspCommand.Disconnect => "Disconnect AirPods",
        ClaspCommand.OutputAirPods => "Switch sound to AirPods",
        ClaspCommand.OutputPrevious => "Return sound to previous speakers",
        ClaspCommand.FixConnection => "Fix connection",
        ClaspCommand.ShowFlyout => "Show Clasp",
        ClaspCommand.ShowSettings => "Open settings",
        ClaspCommand.PlayPause => "Play or pause",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    /// <summary>The link that triggers the command, for example <c>clasp://output/airpods</c>.</summary>
    public static string Link(ClaspCommand command) => command switch
    {
        ClaspCommand.Connect => "clasp://connect",
        ClaspCommand.Disconnect => "clasp://disconnect",
        ClaspCommand.OutputAirPods => "clasp://output/airpods",
        ClaspCommand.OutputPrevious => "clasp://output/previous",
        ClaspCommand.FixConnection => "clasp://fix",
        ClaspCommand.ShowFlyout => "clasp://show",
        ClaspCommand.ShowSettings => "clasp://settings",
        ClaspCommand.PlayPause => "clasp://playpause",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
}
