namespace AppleDeviceHub.Core.Presentation;

/// <summary>When the welcome guide opens by itself at startup. Kept free of UI so it can be tested.</summary>
public static class WelcomePolicy
{
    /// <summary>
    /// The guide opens on its own only on the first launch of a new install, on a real (non-demo) transport, and
    /// never on a quiet sign-in start or a link/command launch. An explicit request (<c>--show=welcome</c>) always opens it.
    /// </summary>
    public static bool ShouldShowAtStartup(bool welcomeCompleted, bool isDemoTransport, bool quietStart, bool isCommandLaunch, bool explicitRequest)
    {
        if (explicitRequest) return !quietStart;
        return !welcomeCompleted && !isDemoTransport && !quietStart && !isCommandLaunch;
    }

    /// <summary>Reads <c>--welcome-page=N</c> (1 to 4) for screenshots; anything else gives page 1.</summary>
    public static int ParseStartPage(IEnumerable<string> arguments, int pageCount = 4)
    {
        const string prefix = "--welcome-page=";
        foreach (var argument in arguments)
        {
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(argument.AsSpan(prefix.Length), out var page) && page >= 1 && page <= pageCount) return page - 1;
        }
        return 0;
    }
}
