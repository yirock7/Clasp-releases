using System.Globalization;

namespace DeviceIdentityInventory.Analysis;

public static class OutputFolder
{
    public static readonly string DefaultRoot = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Clasp", "build-cache", "identity-inventory");

    public static string DefaultFor(DateTimeOffset startedUtc)
        => Path.Combine(DefaultRoot, startedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    public static (string? Path, string? Error) Resolve(
        string? requested,
        DateTimeOffset startedUtc,
        IEnumerable<string> repositoryRoots)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoots);

        string full;
        try
        {
            full = Path.GetFullPath(string.IsNullOrWhiteSpace(requested) ? DefaultFor(startedUtc) : requested);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "The results folder path is not valid.");
        }

        if (repositoryRoots.Any(root => IsSameOrInside(full, root)) || HasGitMarkerInAncestors(full))
            return (null, "The results folder is inside a Git repository. Choose a folder outside all repositories.");
        if (File.Exists(full))
            return (null, "The results path is a file, not a folder.");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            return (null, "The results folder already exists and is not empty. Choose a new or empty folder.");

        return (full, null);
    }

    public static IReadOnlyList<string> FindRepositoryRoots(params string[] startFolders)
    {
        var found = new List<string>();
        foreach (string start in startFolders)
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            DirectoryInfo? directory;
            try { directory = new DirectoryInfo(Path.GetFullPath(start)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            for (; directory is not null; directory = directory.Parent)
            {
                string marker = Path.Combine(directory.FullName, ".git");
                if ((Directory.Exists(marker) || File.Exists(marker))
                    && !found.Contains(directory.FullName, StringComparer.OrdinalIgnoreCase))
                    found.Add(directory.FullName);
            }
        }

        return found;
    }

    private static bool IsSameOrInside(string path, string root)
    {
        string candidate = Normalize(path);
        string normalizedRoot = Normalize(root);
        if (candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasGitMarkerInAncestors(string path)
    {
        DirectoryInfo? directory;
        try { directory = new DirectoryInfo(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        for (; directory is not null; directory = directory.Parent)
        {
            string marker = Path.Combine(directory.FullName, ".git");
            if (File.Exists(marker) || Directory.Exists(marker))
                return true;
        }
        return false;
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}
