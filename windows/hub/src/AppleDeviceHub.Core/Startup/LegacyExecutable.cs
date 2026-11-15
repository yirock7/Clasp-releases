namespace AppleDeviceHub.Core.Startup;

/// <summary>
/// Clasp's program used to be called <c>AppleDeviceHub.exe</c>. An upgrade installs <c>Clasp.exe</c> and deletes the old file, so
/// a per-user entry written by the old version (the sign-in value, the clasp: link command) would point at nothing. This rewrites
/// such a command to the current program, but only when it names the old program in the same folder as the running one, so an
/// entry that belongs to some other copy or some other program is never touched.
/// </summary>
public static class LegacyExecutable
{
    public const string FileName = "AppleDeviceHub.exe";

    /// <summary>
    /// The command with its quoted program path replaced by <paramref name="currentExePath"/> (arguments kept), or null when the
    /// command does not start with the old program in the running program's folder.
    /// </summary>
    public static string? Rewrite(string? command, string? currentExePath)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(currentExePath)) return null;
        if (currentExePath.Contains('"', StringComparison.Ordinal)) return null;
        var trimmed = command.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '"') return null;
        var close = trimmed.IndexOf('"', 1);
        if (close < 0) return null;

        var oldPath = trimmed[1..close];
        var rest = trimmed[(close + 1)..];
        var currentName = Path.GetFileName(currentExePath);
        if (string.Equals(currentName, FileName, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(Path.GetFileName(oldPath), FileName, StringComparison.OrdinalIgnoreCase)) return null;

        var oldFolder = Path.GetDirectoryName(oldPath);
        var currentFolder = Path.GetDirectoryName(currentExePath);
        if (oldFolder is null || currentFolder is null) return null;
        if (!string.Equals(oldFolder.TrimEnd('\\', '/'), currentFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;

        return "\"" + currentExePath + "\"" + rest;
    }
}
