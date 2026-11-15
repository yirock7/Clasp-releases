namespace AppleDeviceHub.Core.Storage;

/// <summary>How a run of <see cref="AppDataFolder.Migrate"/> ended. Names say what happened; none of them carry a path.</summary>
public enum DataFolderMigrationOutcome
{
    /// <summary>There is no old folder, so there was nothing to do.</summary>
    NothingToMigrate = 0,

    /// <summary>The whole old folder was renamed to the new name.</summary>
    Moved = 1,

    /// <summary>The state files were copied across and the old folder was removed.</summary>
    Copied = 2,

    /// <summary>Everything was copied across, but the old folder could not be removed yet (something in it is still in use). It is retried on a later launch.</summary>
    CopiedOldFolderRemains = 3,

    /// <summary>At least one file could not be copied. Nothing was deleted; the next launch tries again.</summary>
    CopyIncomplete = 4,

    /// <summary>An earlier launch finished copying; this launch removed the leftover old folder.</summary>
    OldFolderRemoved = 5,

    /// <summary>An earlier launch finished copying; the old folder still could not be removed.</summary>
    OldFolderRemovalPending = 6,
}

/// <summary>
/// Where Clasp keeps its data (settings, battery history, hub.log, crash marker, update downloads) and the one-time move from the
/// folder it used before the rename (<c>%LOCALAPPDATA%\AppleDeviceHub</c> to <c>%LOCALAPPDATA%\Clasp</c>).
/// The rules favour never losing anything: a rename is tried first; if that fails (for example a setup program that has just
/// finished an update is still running from the old <c>updates</c> folder) the state files are copied over (replacing any
/// partial copy from an earlier attempt), the old folder is deleted only once everything is safely across, a run whose copy was
/// incomplete keeps using the old folder, and a deletion that fails is retried on a later launch. Download caches (<c>updates</c>) are never carried over.
/// </summary>
public static class AppDataFolder
{
    public const string FolderName = "Clasp";
    public const string LegacyFolderName = "AppleDeviceHub";

    /// <summary>Written into the new folder once every state file has been copied from the old one; only then may the old folder be deleted.</summary>
    public const string MigratedMarkerName = "migrated-from-appledevicehub.txt";

    private const string UpdatesFolderName = "updates";

    public static string DefaultRoot { get; } = RootUnder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string LegacyRoot { get; } = LegacyRootUnder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static volatile string? _current;

    /// <summary>
    /// The folder this run reads and writes its data in. It is <see cref="DefaultRoot"/> unless startup decided otherwise (see
    /// <see cref="ChooseRoot"/>); set it once, right after the migration, and read it lazily everywhere else.
    /// </summary>
    public static string Current
    {
        get => _current ?? DefaultRoot;
        set => _current = value;
    }

    /// <summary>
    /// Which folder a run should use after a migration. Until a migration is confirmed complete the old folder is the single
    /// source of truth, so an incomplete copy means this run keeps using the old folder; every other outcome uses the new one.
    /// </summary>
    public static string ChooseRoot(DataFolderMigrationOutcome outcome, string root, string legacyRoot) =>
        outcome == DataFolderMigrationOutcome.CopyIncomplete ? legacyRoot : root;

    public static string RootUnder(string localAppData) => Path.Combine(localAppData, FolderName);

    public static string LegacyRootUnder(string localAppData) => Path.Combine(localAppData, LegacyFolderName);

    /// <summary>Migrates from the default old folder to the default new one. Never throws.</summary>
    public static DataFolderMigrationOutcome MigrateDefault(Action<string>? log = null)
    {
        var outcome = Migrate(DefaultRoot, LegacyRoot, log);
        Current = ChooseRoot(outcome, DefaultRoot, LegacyRoot);
        return outcome;
    }

    /// <summary>
    /// Brings the old folder's state into <paramref name="root"/>. Call it once at startup, after the single-instance check and
    /// before anything reads or writes the data folder. Never throws; <paramref name="log"/> gets one English line without paths.
    /// </summary>
    public static DataFolderMigrationOutcome Migrate(string root, string legacyRoot, Action<string>? log = null)
    {
        try
        {
            if (!Directory.Exists(legacyRoot)) return DataFolderMigrationOutcome.NothingToMigrate;

            // An earlier launch already copied everything: only the removal of the old folder is left.
            if (File.Exists(Path.Combine(root, MigratedMarkerName)))
            {
                if (TryDeleteFolder(legacyRoot))
                {
                    Say(log, "Old data folder removed.");
                    return DataFolderMigrationOutcome.OldFolderRemoved;
                }
                return DataFolderMigrationOutcome.OldFolderRemovalPending;
            }

            if (!Directory.Exists(root))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(root))!);
                    Directory.Move(legacyRoot, root);
                    // Old download caches are not state; drop the one that came along.
                    TryDeleteFolder(Path.Combine(root, UpdatesFolderName));
                    Say(log, "Data folder moved to the new name.");
                    return DataFolderMigrationOutcome.Moved;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Say(log, $"Data folder could not be renamed ({ex.GetType().Name}); copying its files instead.");
                }
            }

            // The new folder may already exist (a log line from a second launch, or a partial copy from an earlier failed attempt).
            // The old folder is the source of truth until the marker is written, so its files overwrite whatever is there.
            Directory.CreateDirectory(root);
            if (!CopyStateFiles(legacyRoot, root))
            {
                Say(log, "Some data files could not be copied from the old folder; nothing was deleted; this session keeps using the old folder and the next launch will try again.");
                return DataFolderMigrationOutcome.CopyIncomplete;
            }

            File.WriteAllText(Path.Combine(root, MigratedMarkerName),
                "Clasp's data was copied from its old folder. The old folder is deleted once nothing in it is in use. You can delete this file." + Environment.NewLine);
            if (TryDeleteFolder(legacyRoot))
            {
                Say(log, "Data files copied to the new folder; old folder removed.");
                return DataFolderMigrationOutcome.Copied;
            }
            Say(log, "Data files copied to the new folder; the old folder is still in use and will be removed on a later launch.");
            return DataFolderMigrationOutcome.CopiedOldFolderRemains;
        }
        catch (Exception ex)
        {
            Say(log, $"Data folder migration failed ({ex.GetType().Name}); the old folder was left as it was.");
            return DataFolderMigrationOutcome.CopyIncomplete;
        }
    }

    /// <summary>Copies every file under <paramref name="from"/> (except the download cache) to <paramref name="to"/>, overwriting copies already there. The diagnostics log is not state, so an existing one is left alone. False if any file failed.</summary>
    private static bool CopyStateFiles(string from, string to)
    {
        var complete = true;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // never follow links out of the folder
            IgnoreInaccessible = false,
        };
        foreach (var source in Directory.EnumerateFiles(from, "*", options))
        {
            var relative = Path.GetRelativePath(from, source);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, 2)[0];
            if (string.Equals(firstSegment, UpdatesFolderName, StringComparison.OrdinalIgnoreCase)) continue;
            var destination = Path.Combine(to, relative);
            try
            {
                if (relative.StartsWith("hub.log", StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }
        return complete;
    }

    private static bool TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Say(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch (Exception) { /* diagnostics must never break the move */ }
    }
}
