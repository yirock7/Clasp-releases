namespace AppleDeviceHub.Core.Updates;

/// <summary>
/// The command line an in-app update gives the installer. Kept here (not in the app) so the exact arguments can be tested.
/// <c>/WAITPID</c> is read by Clasp.iss: Setup waits (up to about 20 s) for that process, the running Clasp, to exit before it
/// replaces any file. <c>/LOG</c> is Inno Setup's own switch and leaves a log of the install for the next failure.
/// </summary>
public static class InstallerLaunch
{
    public static string LogFileName(ClaspVersion version) => $"setup-{version}.log";

    /// <summary>The setup log sits in the updates folder, next to (not inside) the per-version download folder.</summary>
    public static string LogPath(string updatesFolder, ClaspVersion version) => Path.Combine(updatesFolder, LogFileName(version));

    /// <param name="logPath">Full path for Setup's log. It is wrapped in quotes here so a path with spaces survives.</param>
    /// <param name="waitForProcessId">The running Clasp's process id, or 0/negative to skip the wait switch.</param>
    public static IReadOnlyList<string> Arguments(string logPath, int waitForProcessId)
    {
        var arguments = new List<string> { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH" };
        if (waitForProcessId > 0) arguments.Add($"/WAITPID={waitForProcessId}");
        // Inno Setup parses /LOG="path" itself; a quoted value is what lets the path hold spaces.
        arguments.Add($"/LOG=\"{logPath}\"");
        return arguments;
    }

    /// <summary>
    /// The arguments as one raw command-line string. Use this with <c>ProcessStartInfo.Arguments</c>: ArgumentList would
    /// backslash-escape the quotes in <c>/LOG="..."</c>, which Inno Setup does not understand.
    /// </summary>
    public static string CommandLine(string logPath, int waitForProcessId) => string.Join(' ', Arguments(logPath, waitForProcessId));
}
