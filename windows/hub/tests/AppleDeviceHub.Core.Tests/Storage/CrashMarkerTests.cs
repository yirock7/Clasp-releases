using AppleDeviceHub.Core.Storage;
using Xunit;

namespace AppleDeviceHub.Core.Tests.Storage;

public sealed class CrashMarkerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "clasp-crash-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Written_marker_holds_the_type_and_time_but_not_the_message()
    {
        Assert.True(CrashMarker.TryWrite(_folder, Now, new InvalidOperationException("Jane's AirPods Pro")));
        var text = File.ReadAllText(CrashMarker.PathIn(_folder));
        Assert.DoesNotContain("Jane", text);
        var info = CrashMarker.ReadPending(_folder, Now.AddMinutes(1));
        Assert.NotNull(info);
        Assert.Equal("System.InvalidOperationException", info!.ExceptionType);
        Assert.Equal(Now, info.When);
    }

    [Fact]
    public void A_missing_marker_gives_nothing()
        => Assert.Null(CrashMarker.ReadPending(_folder, Now));

    [Fact]
    public void Reading_leaves_a_good_marker_until_it_is_cleared()
    {
        CrashMarker.TryWrite(_folder, Now, new IOException());
        Assert.NotNull(CrashMarker.ReadPending(_folder, Now));
        Assert.True(File.Exists(CrashMarker.PathIn(_folder)));
        Assert.True(CrashMarker.Clear(_folder));
        Assert.Null(CrashMarker.ReadPending(_folder, Now));
        Assert.True(CrashMarker.Clear(_folder)); // clearing nothing is fine
    }

    [Fact]
    public void An_old_marker_is_dropped_without_a_notice()
    {
        CrashMarker.TryWrite(_folder, Now, new IOException());
        Assert.Null(CrashMarker.ReadPending(_folder, Now + CrashMarker.MaxAge + TimeSpan.FromDays(1)));
        Assert.False(File.Exists(CrashMarker.PathIn(_folder)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("time=not a date\ntype=X")]
    [InlineData("")]
    public void An_unreadable_marker_is_dropped_without_a_notice(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(CrashMarker.PathIn(_folder), content);
        Assert.Null(CrashMarker.ReadPending(_folder, Now));
        Assert.False(File.Exists(CrashMarker.PathIn(_folder)));
    }

    [Fact]
    public void An_oversized_marker_is_dropped()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(CrashMarker.PathIn(_folder), "time=" + Now.ToString("O") + "\ntype=" + new string('x', 5000));
        Assert.Null(CrashMarker.ReadPending(_folder, Now));
        Assert.False(File.Exists(CrashMarker.PathIn(_folder)));
    }

    [Fact]
    public void A_marker_dated_in_the_future_is_dropped()
    {
        CrashMarker.TryWrite(_folder, Now.AddDays(3), new IOException());
        Assert.Null(CrashMarker.ReadPending(_folder, Now));
    }

    [Fact]
    public void Odd_characters_in_a_type_name_are_neutralised()
    {
        var text = CrashMarker.Format(Now, "Bad\nname with spaces");
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("Bad?name?with?spaces", CrashMarker.Parse(text)!.ExceptionType);
    }

    [Fact]
    public void Writing_to_an_impossible_folder_returns_false_instead_of_throwing()
        => Assert.False(CrashMarker.TryWrite(Path.Combine(_folder, "a\0b"), Now, null));
}
