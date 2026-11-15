using AppleDeviceHub.Core.Storage;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Storage;

public sealed class AppDataFolderTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "clasp-data-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    private string Old => AppDataFolder.LegacyRootUnder(_base);
    private string New => AppDataFolder.RootUnder(_base);

    public AppDataFolderTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    private static void Put(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Folder_names_are_the_new_and_old_names_under_local_app_data()
    {
        Assert.Equal(Path.Combine(_base, "Clasp"), New);
        Assert.Equal(Path.Combine(_base, "AppleDeviceHub"), Old);
        Assert.Equal("Clasp", Path.GetFileName(AppDataFolder.DefaultRoot));
    }

    [Fact]
    public void Nothing_to_do_without_an_old_folder()
    {
        Assert.Equal(DataFolderMigrationOutcome.NothingToMigrate, AppDataFolder.Migrate(New, Old, _log.Add));
        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void An_old_folder_is_renamed_when_there_is_no_new_one_and_its_download_cache_is_dropped()
    {
        Put(Old, "settings.json", "S");
        Put(Old, "settings.json.bak", "B");
        Put(Old, "battery.txt", "H");
        Put(Old, Path.Combine("updates", "Clasp-0.4-setup.exe"), "installer");

        Assert.Equal(DataFolderMigrationOutcome.Moved, AppDataFolder.Migrate(New, Old, _log.Add));

        Assert.False(Directory.Exists(Old));
        Assert.Equal("S", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(New, "settings.json.bak")));
        Assert.Equal("H", File.ReadAllText(Path.Combine(New, "battery.txt")));
        Assert.False(Directory.Exists(Path.Combine(New, "updates")));
    }

    [Fact]
    public void Log_lines_never_contain_paths()
    {
        Put(Old, "settings.json", "S");
        AppDataFolder.Migrate(New, Old, _log.Add);
        Assert.NotEmpty(_log);
        Assert.All(_log, line => Assert.DoesNotContain(_base, line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_existing_new_folder_gets_only_the_missing_files_and_nothing_is_overwritten()
    {
        Put(Old, "settings.json", "old settings");
        Put(Old, "battery.txt", "old history");
        Put(Old, Path.Combine("sub", "other.txt"), "other");
        Put(Old, Path.Combine("updates", "setup.exe"), "installer");
        Put(New, "hub.log", "new log");
        Put(New, "battery.txt", "newer history");

        Assert.Equal(DataFolderMigrationOutcome.Copied, AppDataFolder.Migrate(New, Old, _log.Add));

        Assert.Equal("old settings", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal("newer history", File.ReadAllText(Path.Combine(New, "battery.txt")));
        Assert.Equal("new log", File.ReadAllText(Path.Combine(New, "hub.log")));
        Assert.Equal("other", File.ReadAllText(Path.Combine(New, "sub", "other.txt")));
        Assert.False(Directory.Exists(Path.Combine(New, "updates")));
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void A_locked_update_installer_blocks_the_rename_but_never_loses_settings_and_cleanup_is_retried()
    {
        Put(Old, "settings.json", "S");
        Put(Old, "settings.json.bak", "B");
        Put(Old, "battery.txt", "H");
        var installer = Path.Combine(Old, "updates", "Clasp-0.4-setup.exe");
        Put(Old, Path.Combine("updates", "Clasp-0.4-setup.exe"), "running installer");

        DataFolderMigrationOutcome first;
        using (new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = AppDataFolder.Migrate(New, Old, _log.Add);
            Assert.Equal("S", File.ReadAllText(Path.Combine(New, "settings.json")));
            Assert.Equal("B", File.ReadAllText(Path.Combine(New, "settings.json.bak")));
            Assert.Equal("H", File.ReadAllText(Path.Combine(New, "battery.txt")));
            Assert.True(Directory.Exists(Old));
        }
        Assert.Equal(DataFolderMigrationOutcome.CopiedOldFolderRemains, first);
        Assert.True(File.Exists(Path.Combine(New, AppDataFolder.MigratedMarkerName)));

        // A later launch, once the installer has finished: the leftover old folder goes.
        Assert.Equal(DataFolderMigrationOutcome.OldFolderRemoved, AppDataFolder.Migrate(New, Old, _log.Add));
        Assert.False(Directory.Exists(Old));
        Assert.Equal("S", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.Equal(DataFolderMigrationOutcome.NothingToMigrate, AppDataFolder.Migrate(New, Old, _log.Add));
    }

    [Fact]
    public void While_the_old_folder_is_still_locked_the_retry_keeps_waiting_without_touching_settings()
    {
        Put(Old, "settings.json", "S");
        var installer = Path.Combine(Old, "updates", "setup.exe");
        Put(Old, Path.Combine("updates", "setup.exe"), "x");
        using var held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(DataFolderMigrationOutcome.CopiedOldFolderRemains, AppDataFolder.Migrate(New, Old, _log.Add));
        File.WriteAllText(Path.Combine(New, "settings.json"), "edited in the new folder");
        Assert.Equal(DataFolderMigrationOutcome.OldFolderRemovalPending, AppDataFolder.Migrate(New, Old, _log.Add));
        Assert.Equal("edited in the new folder", File.ReadAllText(Path.Combine(New, "settings.json")));
    }

    [Fact]
    public void A_file_that_cannot_be_copied_keeps_the_old_folder_and_is_retried()
    {
        Put(Old, "settings.json", "S");
        Put(Old, "battery.txt", "H");
        Put(New, "hub.log", "x"); // forces the copy path
        var locked = Path.Combine(Old, "battery.txt");

        DataFolderMigrationOutcome first;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = AppDataFolder.Migrate(New, Old, _log.Add);
        }
        Assert.Equal(DataFolderMigrationOutcome.CopyIncomplete, first);
        Assert.True(Directory.Exists(Old));
        Assert.False(File.Exists(Path.Combine(New, AppDataFolder.MigratedMarkerName)));
        Assert.Equal("S", File.ReadAllText(Path.Combine(New, "settings.json")));

        Assert.Equal(DataFolderMigrationOutcome.Copied, AppDataFolder.Migrate(New, Old, _log.Add));
        Assert.Equal("H", File.ReadAllText(Path.Combine(New, "battery.txt")));
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void A_throwing_log_callback_does_not_break_the_migration()
    {
        Put(Old, "settings.json", "S");
        Assert.Equal(DataFolderMigrationOutcome.Moved, AppDataFolder.Migrate(New, Old, _ => throw new InvalidOperationException()));
        Assert.True(File.Exists(Path.Combine(New, "settings.json")));
    }
}
