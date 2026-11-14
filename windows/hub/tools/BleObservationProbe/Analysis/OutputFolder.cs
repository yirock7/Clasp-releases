using System.Globalization;

namespace BleObservationProbe.Analysis;

/// <summary>Chooses and checks the per-run results folder. Never inside a git working tree.</summary>
public static class OutputFolder
{
    /// <summary>Default parent folder for results (outside the repository).</summary>
    public static readonly string DefaultRoot = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Clasp", "build-cache", "ble-probe");

    public static string DefaultFor(DateTimeOffset startUtc, string? root = null)
        => Path.Combine(root ?? DefaultRoot, startUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    /// <summary>
    /// Resolve and check the results folder. Returns the full path, or an error. Does not create it.
    /// Refuses a folder inside any of <paramref name="forbiddenRoots"/> (repository roots) and an
    /// existing folder that is not empty (an existing file at that path is refused too).
    /// </summary>
    public static (string? Path, string? Error) Resolve(string? requested, DateTimeOffset startUtc, IEnumerable<string> forbiddenRoots, string? defaultRoot = null)
    {
        ArgumentNullException.ThrowIfNull(forbiddenRoots);
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(requested) ? DefaultFor(startUtc, defaultRoot) : requested);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"The results folder path is not valid ({ex.Message}).");
        }

        foreach (var root in forbiddenRoots)
        {
            if (IsSameOrInside(full, root))
                return (null, $"The results folder '{full}' is inside the repository '{root}'. Choose a folder outside it.");
        }

        if (File.Exists(full)) return (null, $"'{full}' is a file, not a folder.");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            return (null, $"The results folder '{full}' already exists and is not empty. Choose a new or empty folder.");
        return (full, null);
    }

    public static bool IsSameOrInside(string path, string root)
    {
        string p = Normalise(path), r = Normalise(root);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Walk up from each start folder and return every folder that contains a .git entry.</summary>
    public static IReadOnlyList<string> FindRepositoryRoots(params string[] startFolders)
    {
        var found = new List<string>();
        foreach (var start in startFolders)
        {
            if (string.IsNullOrEmpty(start)) continue;
            DirectoryInfo? dir;
            try { dir = new DirectoryInfo(System.IO.Path.GetFullPath(start)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            for (; dir is not null; dir = dir.Parent)
            {
                string git = System.IO.Path.Combine(dir.FullName, ".git");
                if ((Directory.Exists(git) || File.Exists(git)) && !found.Contains(dir.FullName, StringComparer.OrdinalIgnoreCase))
                    found.Add(dir.FullName);
            }
        }
        return found;
    }

    private static string Normalise(string path)
        => System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
}
