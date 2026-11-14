using AppleDeviceHub.Core.Presentation;

namespace AppleDeviceHub.Core.Tests.Presentation;

public class HubSettingsStoreTests
{
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clasp-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void A_settings_file_from_0_1_16_with_a_glass_style_key_still_loads()
    {
        // 0.1.16 wrote "GlassStyle": "Clear" | "Frosted". The setting is gone; the key must be ignored, not an error.
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            foreach (var style in new[] { "Clear", "Frosted", "Mirror" })
            {
                File.WriteAllText(path, "{ \"Theme\": 2, \"ReduceMotion\": true, \"ReduceTransparency\": false, \"AutoShowCaseCard\": true, \"CaseCardSeconds\": 9, \"GlassStyle\": \"" + style + "\" }");
                var loaded = HubSettingsStore.Load(path);
                Assert.Equal(AppTheme.Dark, loaded.Theme);
                Assert.True(loaded.ReduceMotion);
                Assert.Equal(9, loaded.CaseCardSeconds);
            }
            Assert.Empty(Directory.GetFiles(folder, "*.corrupt-*")); // not treated as unreadable
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_brand_new_install_has_not_seen_the_welcome_guide()
    {
        var folder = NewFolder();
        try
        {
            var loaded = HubSettingsStore.Load(Path.Combine(folder, "settings.json")); // no file, no backup
            Assert.False(loaded.WelcomeCompleted);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_settings_file_from_an_older_version_counts_as_welcome_completed()
    {
        // Older versions never wrote WelcomeCompleted. An upgrading user must not get the guide unasked.
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, "{ \"Theme\": 1, \"ReduceMotion\": false, \"ReduceTransparency\": false, \"AutoShowCaseCard\": true, \"CaseCardSeconds\": 6 }");
            Assert.True(HubSettingsStore.Load(path).WelcomeCompleted);

            // The same holds for the backup copy when settings.json itself is missing.
            File.Move(path, path + ".bak");
            Assert.True(HubSettingsStore.Load(path).WelcomeCompleted);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void An_unreadable_settings_file_does_not_bring_the_welcome_guide_back()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, "{ not json");
            Assert.True(HubSettingsStore.Load(path).WelcomeCompleted);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void The_welcome_flag_survives_a_save_and_load_in_both_states()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            // A fresh install that saved something else before finishing the guide still shows it next time.
            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 9 }, path));
            Assert.False(HubSettingsStore.Load(path).WelcomeCompleted);

            Assert.True(HubSettingsStore.Save(new HubSettings { WelcomeCompleted = true }, path));
            Assert.True(HubSettingsStore.Load(path).WelcomeCompleted);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Saving_twice_keeps_the_first_save_as_the_backup()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 9 }, path));
            var first = File.ReadAllText(path);
            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 12 }, path));

            Assert.Equal(first, File.ReadAllText(path + ".bak"));
            Assert.Equal(12, HubSettingsStore.Load(path).CaseCardSeconds);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_corrupt_file_is_kept_and_the_backup_is_used()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 9 }, path));
            File.Copy(path, path + ".bak");
            File.WriteAllText(path, "{ not json");
            var messages = new List<string>();

            var loaded = HubSettingsStore.Load(path, messages.Add);

            Assert.Equal(9, loaded.CaseCardSeconds);
            var kept = Assert.Single(Directory.GetFiles(folder, "settings.corrupt-*.json"));
            Assert.Equal("{ not json", File.ReadAllText(kept));
            Assert.Equal("{ not json", File.ReadAllText(path)); // Load never overwrites the original
            Assert.Contains(messages, m => m.Contains("restored from backup"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Only_the_three_newest_corrupt_copies_are_kept()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            foreach (var stamp in new[] { "20260101-000001", "20260102-000001", "20260103-000001", "20260104-000001", "20260105-000001" })
                File.WriteAllText(Path.Combine(folder, $"settings.corrupt-{stamp}.json"), "{ old");
            File.WriteAllText(path, "{ not json");

            HubSettingsStore.Load(path); // makes a fresh timestamped copy and trims

            var kept = Directory.GetFiles(folder, "settings.corrupt-*.json").Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(3, kept.Count);
            Assert.DoesNotContain("settings.corrupt-20260101-000001.json", kept);
            Assert.DoesNotContain("settings.corrupt-20260102-000001.json", kept);
            Assert.True(File.Exists(path)); // the real file is never touched
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_missing_file_is_restored_from_the_backup()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 11 }, path));
            File.Move(path, path + ".bak");
            var messages = new List<string>();

            var loaded = HubSettingsStore.Load(path, messages.Add);

            Assert.Equal(11, loaded.CaseCardSeconds);
            Assert.Contains("Settings restored from backup (settings.json was missing).", messages);
            Assert.Empty(Directory.GetFiles(folder, "settings.corrupt-*.json"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_corrupt_file_without_a_backup_gives_defaults_and_is_not_overwritten()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            File.WriteAllText(path, "garbage");
            var messages = new List<string>();

            var loaded = HubSettingsStore.Load(path, messages.Add);

            Assert.Equal(new HubSettings().CaseCardSeconds, loaded.CaseCardSeconds);
            var kept = Assert.Single(Directory.GetFiles(folder, "settings.corrupt-*.json"));
            Assert.Equal("garbage", File.ReadAllText(kept));
            Assert.Equal("garbage", File.ReadAllText(path));
            var message = Assert.Single(messages);
            Assert.StartsWith("Settings could not be read; starting with defaults.", message);
            Assert.Contains(Path.GetFileName(kept), message);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_corrupt_file_does_not_replace_a_good_backup_when_saving()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 9 }, path));
            File.Copy(path, path + ".bak");
            var good = File.ReadAllText(path + ".bak");
            File.WriteAllText(path, "{ not json");

            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 4 }, path));

            Assert.Equal(good, File.ReadAllText(path + ".bak"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Artwork_colorways_round_trip_per_device_and_model()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            var settings = new HubSettings();
            settings.SetArtworkColorway("device|one", 0x2420, "midnight");
            settings.SetArtworkColorway("device|one", 0x1420, "white");
            settings.SetArtworkColorway("device|two", 0x2420, "starlight");

            Assert.True(HubSettingsStore.Save(settings, path));
            var loaded = HubSettingsStore.Load(path);

            Assert.Equal("midnight", loaded.GetArtworkColorway("device|one", 0x2420));
            Assert.Equal("white", loaded.GetArtworkColorway("device|one", 0x1420));
            Assert.Equal("starlight", loaded.GetArtworkColorway("device|two", 0x2420));
            Assert.Equal("white", loaded.GetArtworkColorway("device|two", 0x1420)); // model-wide fallback

            loaded.SetArtworkColorway("device|one", 0x2420, null);
            Assert.True(HubSettingsStore.Save(loaded, path));
            Assert.Equal("starlight", HubSettingsStore.Load(path).GetArtworkColorway("device|one", 0x2420)); // model-wide remains
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Artwork_colorway_falls_back_to_model_wide_when_the_device_id_is_missing_or_different()
    {
        var settings = new HubSettings();
        settings.SetArtworkColorway("windows-id", 0x2420, "midnight");

        Assert.Equal("midnight", settings.GetArtworkColorway(null, 0x2420));
        Assert.Equal("midnight", settings.GetArtworkColorway("transport-id", 0x2420));
        Assert.Null(settings.GetArtworkColorway(null, 0x1420));
    }

    [Fact]
    public void Artwork_colorway_device_specific_value_wins_over_model_wide()
    {
        var settings = new HubSettings();
        settings.SetArtworkColorway("a", 0x2420, "midnight");
        settings.SetArtworkColorway("b", 0x2420, "starlight");

        Assert.Equal("midnight", settings.GetArtworkColorway("a", 0x2420));
        Assert.Equal("starlight", settings.GetArtworkColorway("b", 0x2420));
        Assert.Equal("starlight", settings.GetArtworkColorway("c", 0x2420));

        settings.SetArtworkColorway(null, 0x2420, null); // no device id clears the model-wide entry
        Assert.Null(settings.GetArtworkColorway("c", 0x2420));
        Assert.Equal("midnight", settings.GetArtworkColorway("a", 0x2420));
    }

    [Fact]
    public void Artwork_colorways_are_capped_dropping_the_oldest_inserted()
    {
        var settings = new HubSettings();
        for (ushort model = 1; model <= 40; model++) settings.SetArtworkColorway(null, model, "c" + model);

        Assert.Equal(HubSettings.MaxArtworkColorways, settings.ArtworkColorways.Count);
        Assert.Null(settings.GetArtworkColorway(null, 8));
        Assert.Equal("c9", settings.GetArtworkColorway(null, 9));
        Assert.Equal("c40", settings.GetArtworkColorway(null, 40));

        // device + model-wide are two entries each
        var pairs = new HubSettings();
        for (ushort model = 1; model <= 20; model++) pairs.SetArtworkColorway("d", model, "c" + model);
        Assert.Equal(HubSettings.MaxArtworkColorways, pairs.ArtworkColorways.Count);
        Assert.Null(pairs.GetArtworkColorway("d", 4));
        Assert.Equal("c5", pairs.GetArtworkColorway("d", 5));
    }
}
