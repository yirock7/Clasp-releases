using System.Globalization;

namespace GattDiscoveryProbe.Analysis;

/// <summary>Chooses and checks the result folder without creating it.</summary>
public static class OutputFolder
{
    public static readonly string DefaultRoot = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Clasp", "build-cache", "gatt-probe");

    public static string DefaultFor(DateTimeOffset startUtc, string? root = null)
        => Path.Combine(root ?? DefaultRoot, startUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    /// <summary>
    /// Resolve output location with injectable existence checks so the rules can be tested without
    /// touching the machine. The runtime defaults inspect actual folders and .git markers.
    /// </summary>
    public static (string? Path, string? Error) Resolve(
        string? requested,
        DateTimeOffset startUtc,
        IEnumerable<string> repositoryRoots,
        string? defaultRoot = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? directoryHasEntries = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoots);
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        directoryHasEntries ??= path => Directory.EnumerateFileSystemEntries(path).Any();

        string full;
        try
        {
            full = Path.GetFullPath(string.IsNullOrWhiteSpace(requested) ? DefaultFor(startUtc, defaultRoot) : requested);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "The results folder path is not valid.");
        }

        string[] roots = repositoryRoots.ToArray();
        if (roots.Any(root => IsSameOrInside(full, root)) || HasGitMarkerInAncestors(full, fileExists, directoryExists))
            return (null, "The results folder is inside a Git repository. Choose a folder outside all repositories.");

        if (fileExists(full))
            return (null, "The results path is a file, not a folder.");
        if (directoryExists(full) && directoryHasEntries(full))
            return (null, "The results folder already exists and is not empty. Choose a new or empty folder.");

        return (full, null);
    }

    public static bool IsSameOrInside(string path, string root)
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

    /// <summary>Walk up from application and working folders, including .git files used by worktrees.</summary>
    public static IReadOnlyList<string> FindRepositoryRoots(params string[] startFolders)
    {
        var found = new List<string>();
        foreach (string start in startFolders)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            DirectoryInfo? directory;
            try { directory = new DirectoryInfo(Path.GetFullPath(start)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            for (; directory is not null; directory = directory.Parent)
            {
                string gitMarker = Path.Combine(directory.FullName, ".git");
                if ((Directory.Exists(gitMarker) || File.Exists(gitMarker))
                    && !found.Contains(directory.FullName, StringComparer.OrdinalIgnoreCase))
                    found.Add(directory.FullName);
            }
        }
        return found;
    }

    private static bool HasGitMarkerInAncestors(string path, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        DirectoryInfo? directory;
        try { directory = new DirectoryInfo(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        for (; directory is not null; directory = directory.Parent)
        {
            string marker = Path.Combine(directory.FullName, ".git");
            if (fileExists(marker) || directoryExists(marker)) return true;
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
