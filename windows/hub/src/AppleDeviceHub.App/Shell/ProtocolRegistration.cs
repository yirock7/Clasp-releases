using Microsoft.Win32;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Per-user registration of the clasp: link scheme under HKEY_CURRENT_USER\Software\Classes\clasp,
/// so no administrator rights are needed and other users on the PC are unaffected. Never touches
/// HKEY_LOCAL_MACHINE. Windows then runs <c>"&lt;exe&gt;" --uri="%1"</c> for a clasp:// link.
/// Nothing calls this on its own: the app does so only when the user turns the link setting on
/// (and removes it when they turn it off).
/// </summary>
internal static class ProtocolRegistration
{
    private const string SchemeKeyPath = @"Software\Classes\clasp";
    private const string CommandKeyPath = SchemeKeyPath + @"\shell\open\command";
    private const string OwnerMarker = "--uri=";

    /// <summary>The exact command line stored for an executable path.</summary>
    internal static string BuildCommand(string exePath) => $"\"{exePath}\" {OwnerMarker}\"%1\"";

    /// <summary>Writes the registration. Returns false (never throws) if the path is unusable or the registry refuses.</summary>
    public static bool Register(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath) || exePath.Contains('"')) return false;
        try
        {
            using var scheme = Registry.CurrentUser.CreateSubKey(SchemeKeyPath, writable: true);
            scheme.SetValue("", "URL:Clasp link");
            scheme.SetValue("URL Protocol", "");
            using (var icon = scheme.CreateSubKey("DefaultIcon", writable: true))
                icon.SetValue("", $"\"{exePath}\",0");
            using var command = scheme.CreateSubKey(@"shell\open\command", writable: true);
            command.SetValue("", BuildCommand(exePath));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LocalLog.Write($"clasp: registration failed ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// Removes the registration, but only if it is ours (its command contains --uri=); a clasp key
    /// created by some other program is left alone. Returns true when nothing of ours remains.
    /// </summary>
    public static bool Unregister()
    {
        try
        {
            if (ReadCommand() is { } command && !command.Contains(OwnerMarker, StringComparison.OrdinalIgnoreCase))
                return false;
            Registry.CurrentUser.DeleteSubKeyTree(SchemeKeyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LocalLog.Write($"clasp: unregistration failed ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// True when the per-user key exists and its command is exactly ours. With
    /// <paramref name="exePath"/>, also requires it to point at that executable (so a moved or
    /// reinstalled copy shows as not registered and can be refreshed).
    /// </summary>
    public static bool IsRegistered(string? exePath = null)
    {
        try
        {
            var command = ReadCommand();
            if (command is null) return false;
            return exePath is null
                ? command.Contains(OwnerMarker, StringComparison.OrdinalIgnoreCase)
                : string.Equals(command, BuildCommand(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath, writable: false);
        return key?.GetValue("") as string;
    }
}
