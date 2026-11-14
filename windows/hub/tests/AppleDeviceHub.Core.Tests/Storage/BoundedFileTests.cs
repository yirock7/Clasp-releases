using System.Text;
using AppleDeviceHub.Core.Battery;
using AppleDeviceHub.Core.Tests.Battery;
using AppleDeviceHub.Core.Presentation;
using AppleDeviceHub.Core.Storage;

namespace AppleDeviceHub.Core.Tests.Storage;

public class BoundedFileTests
{
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clasp-bounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void A_file_within_the_limit_is_read_including_a_byte_order_mark()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "a.txt");
            File.WriteAllText(path, "hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert.Equal(BoundedReadStatus.Ok, BoundedFile.ReadText(path, 100, out var text));
            Assert.Equal("hello", text);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_file_exactly_at_the_limit_is_read_and_one_byte_more_is_refused()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "a.txt");
            File.WriteAllText(path, new string('x', 50));
            Assert.Equal(BoundedReadStatus.Ok, BoundedFile.ReadText(path, 50, out var text));
            Assert.Equal(50, text!.Length);
            Assert.Equal(BoundedReadStatus.TooLarge, BoundedFile.ReadText(path, 49, out var none));
            Assert.Null(none);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_missing_file_is_reported_as_missing()
        => Assert.Equal(BoundedReadStatus.Missing,
            BoundedFile.ReadText(Path.Combine(Path.GetTempPath(), "clasp-none-" + Guid.NewGuid().ToString("N")), 10, out _));

    [Fact]
    public void Moving_aside_renames_the_file_and_keeps_only_the_newest_three()
    {
        var folder = NewFolder();
        try
        {
            foreach (var stamp in new[] { "20260101-000001", "20260102-000001", "20260103-000001" })
                File.WriteAllText(Path.Combine(folder, $"data.corrupt-{stamp}.txt"), "old");
            var path = Path.Combine(folder, "data.txt");
            File.WriteAllText(path, "big");

            var name = BoundedFile.MoveAside(path);

            Assert.NotNull(name);
            Assert.False(File.Exists(path));
            Assert.Equal("big", File.ReadAllText(Path.Combine(folder, name!)));
            Assert.Equal(BoundedFile.MaxKeptAside, Directory.GetFiles(folder, "data.corrupt-*.txt").Length);
            Assert.False(File.Exists(Path.Combine(folder, "data.corrupt-20260101-000001.txt")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void An_oversized_settings_file_is_moved_aside_and_not_parsed()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            // Valid JSON, but far over the limit: it must be refused on size alone.
            File.WriteAllText(path, "{ \"CaseCardSeconds\": 9, \"PerAppRulesText\": \"" + new string('a', (int)BoundedFile.SettingsMaxBytes + 10) + "\" }");
            var messages = new List<string>();

            var loaded = HubSettingsStore.Load(path, messages.Add);

            Assert.NotEqual(9, loaded.CaseCardSeconds);
            Assert.True(loaded.WelcomeCompleted); // a file existed: this is not a new install
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "settings.corrupt-*.json"));
            Assert.Contains(messages, m => m.Contains("oversized"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void An_oversized_settings_file_falls_back_to_the_backup()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            Assert.True(HubSettingsStore.Save(new HubSettings { CaseCardSeconds = 9 }, path));
            File.Copy(path, path + ".bak");
            File.WriteAllText(path, new string(' ', (int)BoundedFile.SettingsMaxBytes + 1));

            var loaded = HubSettingsStore.Load(path);

            Assert.Equal(9, loaded.CaseCardSeconds);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Loading_battery_history_keeps_only_the_newest_samples_and_devices_while_parsing()
    {
        var options = new BatteryHistoryOptions { MaxSamplesPerDevice = 5, MaxDevices = 2 };
        var t0 = B.T0.ToUnixTimeSeconds();
        var text = new StringBuilder("clasp-battery-history 1\n");
        // Three devices, the middle one updated most recently; device "a" has 50 samples.
        text.Append("Da\n");
        for (var i = 0; i < 50; i++) text.Append(t0 + i).Append(",80,80,,1,2\n");
        text.Append("Db\n").Append(t0 + 1000).Append(",70,70,,1,2\n");
        text.Append("Dc\n").Append(t0 + 500).Append(",60,60,,1,2\n");
        var store = new InMemoryBatteryHistoryStore();
        store.Write(BatteryHistory.StoreName, text.ToString());

        var history = new BatteryHistory(store, options, B.Clock);
        history.Load();

        Assert.Empty(history.Samples("a")); // least recently updated device went first
        Assert.Single(history.Samples("b"));
        Assert.Single(history.Samples("c"));

        // And the per-device cap holds: only the newest five of a long list survive.
        var one = new StringBuilder("clasp-battery-history 1\nDa\n");
        for (var i = 0; i < 50; i++) one.Append(t0 + i).Append(",80,80,,1,2\n");
        store.Write(BatteryHistory.StoreName, one.ToString());
        var capped = new BatteryHistory(store, options, B.Clock);
        capped.Load();
        var samples = capped.Samples("a");
        Assert.Equal(5, samples.Count);
        Assert.Equal(t0 + 49, samples[^1].Time.ToUnixTimeSeconds());
    }

    private static BatteryHistory LoadFrom(string text, BatteryHistoryOptions options)
    {
        var store = new InMemoryBatteryHistoryStore();
        store.Write(BatteryHistory.StoreName, text);
        var history = new BatteryHistory(store, options, B.Clock);
        history.Load();
        return history;
    }

    [Fact]
    public void A_newest_first_file_with_too_many_devices_keeps_the_newest_ones()
    {
        var t0 = B.T0.ToUnixTimeSeconds();
        var text = new StringBuilder("clasp-battery-history 1\n");
        // Newest first: d0 is newest ... d4 oldest. MaxDevices is 3, so MaxDevices + 2 devices.
        for (var i = 0; i < 5; i++) text.Append("Dd").Append(i).Append('\n').Append(t0 + 1000 - i * 100).Append(",70,70,,1,2\n");

        var history = LoadFrom(text.ToString(), new BatteryHistoryOptions { MaxDevices = 3 });

        Assert.Single(history.Samples("d0"));
        Assert.Single(history.Samples("d1"));
        Assert.Single(history.Samples("d2"));
        Assert.Empty(history.Samples("d3"));
        Assert.Empty(history.Samples("d4"));
    }

    [Fact]
    public void A_trailing_empty_device_section_evicts_nothing()
    {
        var t0 = B.T0.ToUnixTimeSeconds();
        var text = "clasp-battery-history 1\n"
            + "Da\n" + (t0 + 10) + ",70,70,,1,2\n"
            + "Db\n" + (t0 + 20) + ",70,70,,1,2\n"
            + "Dempty\n";

        var history = LoadFrom(text, new BatteryHistoryOptions { MaxDevices = 2 });

        Assert.Single(history.Samples("a"));
        Assert.Single(history.Samples("b"));
        Assert.Empty(history.Samples("empty"));
    }
}
