using AppleDeviceHub.Core.Battery;

namespace AppleDeviceHub.Core.Tests.Battery;

public class LastCaseMemoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_case_level_survives_a_flush_and_load()
    {
        var store = new InMemoryBatteryHistoryStore();
        var first = new LastCaseMemory(store);
        Assert.True(first.Observe("dev", 70, T0));
        Assert.True(first.IsDirty);
        Assert.True(first.Flush());
        Assert.False(first.IsDirty);

        var second = new LastCaseMemory(store);
        second.Load();
        Assert.Equal(new RememberedCase(70, T0), second.Get("dev"));
        Assert.False(second.IsDirty);
    }

    [Fact]
    public void Clear_forgets_the_remembered_case_and_writes_the_empty_file()
    {
        var store = new InMemoryBatteryHistoryStore();
        var memory = new LastCaseMemory(store);
        memory.Observe("dev", 70, T0);
        memory.Flush();
        memory.Clear();
        Assert.Null(memory.Get("dev"));
        Assert.True(memory.Flush());
        var loaded = new LastCaseMemory(store);
        loaded.Load();
        Assert.Null(loaded.Get("dev"));
    }

    [Fact]
    public void An_older_reading_is_ignored_and_a_newer_one_replaces()
    {
        var memory = new LastCaseMemory(new InMemoryBatteryHistoryStore());
        memory.Observe("dev", 70, T0);
        Assert.False(memory.Observe("dev", 90, T0.AddMinutes(-1)));
        Assert.Equal(70, memory.Get("dev")!.Percent);
        Assert.False(memory.Observe("dev", 70, T0));        // the same reading again is not a change
        Assert.True(memory.Observe("dev", 60, T0.AddHours(2)));
        Assert.Equal(new RememberedCase(60, T0.AddHours(2)), memory.Get("dev"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void An_out_of_range_percent_is_ignored(int percent)
    {
        var memory = new LastCaseMemory(new InMemoryBatteryHistoryStore());
        Assert.False(memory.Observe("dev", percent, T0));
        Assert.Null(memory.Get("dev"));
        Assert.False(memory.IsDirty);
        Assert.False(memory.Observe("", 50, T0));
    }

    [Fact]
    public void Each_device_has_its_own_level_and_odd_ids_survive()
    {
        var store = new InMemoryBatteryHistoryStore();
        var memory = new LastCaseMemory(store);
        memory.Observe("BTHENUM\\Dev_AA,BB 1", 40, T0);
        memory.Observe("other", 80, T0.AddMinutes(5));
        memory.Flush();

        var loaded = new LastCaseMemory(store);
        loaded.Load();
        Assert.Equal(40, loaded.Get("BTHENUM\\Dev_AA,BB 1")!.Percent);
        Assert.Equal(80, loaded.Get("other")!.Percent);
        Assert.Null(loaded.Get("third"));
        Assert.Null(loaded.Get(null));
    }

    [Fact]
    public void The_stored_text_has_no_charging_flag()
    {
        var store = new InMemoryBatteryHistoryStore();
        var memory = new LastCaseMemory(store);
        memory.Observe("dev", 70, T0);
        memory.Flush();
        var lines = store.Read(LastCaseMemory.StoreName)!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("clasp-last-case 1", lines[0]);
        Assert.Equal(3, lines[1].Split(',').Length);   // id, time, percent
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("wrong header 9\ndev,1700000000,50\n")]
    public void Missing_or_damaged_data_loads_as_empty(string text)
    {
        var store = new InMemoryBatteryHistoryStore();
        if (text.Length > 0) store.Write(LastCaseMemory.StoreName, text);
        var memory = new LastCaseMemory(store);
        memory.Load();
        Assert.Null(memory.Get("dev"));
        Assert.True(memory.Flush());                    // nothing to write
    }

    [Fact]
    public void Damaged_lines_are_skipped_and_good_ones_kept()
    {
        var store = new InMemoryBatteryHistoryStore();
        store.Write(LastCaseMemory.StoreName,
            "clasp-last-case 1\nbad\na,notatime,50\nb,1700000000,150\nc,1700000000,-5\nd,1700000000\ne,99999999999999999,50\n%ZZ%,1700000000,50\ngood,1700000000,55\n");
        var memory = new LastCaseMemory(store);
        memory.Load();
        Assert.Equal(55, memory.Get("good")!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), memory.Get("good")!.Time);
        foreach (var id in new[] { "a", "b", "c", "d", "e" }) Assert.Null(memory.Get(id));
    }

    [Fact]
    public void The_number_of_devices_is_bounded_and_the_oldest_go_first()
    {
        var store = new InMemoryBatteryHistoryStore();
        var memory = new LastCaseMemory(store);
        for (int i = 0; i < LastCaseMemory.MaxDevices + 4; i++) memory.Observe($"dev{i}", 50, T0.AddMinutes(i));
        Assert.Null(memory.Get("dev0"));
        Assert.Null(memory.Get("dev3"));
        Assert.NotNull(memory.Get("dev4"));
        Assert.NotNull(memory.Get($"dev{LastCaseMemory.MaxDevices + 3}"));

        // A hand-edited file with too many devices is trimmed on load as well.
        var lines = Enumerable.Range(0, 20).Select(i => $"x{i},{T0.AddMinutes(i).ToUnixTimeSeconds()},50");
        store.Write(LastCaseMemory.StoreName, "clasp-last-case 1\n" + string.Join('\n', lines) + "\n");
        var loaded = new LastCaseMemory(store);
        loaded.Load();
        Assert.Equal(LastCaseMemory.MaxDevices, Enumerable.Range(0, 20).Count(i => loaded.Get($"x{i}") is not null));
        Assert.NotNull(loaded.Get("x19"));
        Assert.Null(loaded.Get("x11"));
    }

    [Fact]
    public void A_failed_write_stays_dirty_and_retries()
    {
        var store = new FailingStore();
        var memory = new LastCaseMemory(store);
        memory.Observe("a", 50, T0);
        Assert.False(memory.Flush());
        Assert.True(memory.IsDirty);
        store.Fail = false;
        Assert.True(memory.Flush());
        Assert.False(memory.IsDirty);
    }

    private sealed class FailingStore : IBatteryHistoryStore
    {
        public bool Fail = true;
        public string? Read(string name) => null;
        public bool Write(string name, string content) => !Fail;
    }
}
